// MixDbgProfiler.cpp — ICorProfilerCallback2 implementation.
//
// This DLL implements ICorProfilerCallback2. When loaded by the CLR (via
// CORECLR_ENABLE_PROFILING=1 env var), it sends JIT compilation notifications
// to MixDbg via a named pipe.

#include "MixDbgProfiler.h"

MixDbgProfiler* g_pProfiler = nullptr;

// ============================================================================
// Constructor / Destructor
// ============================================================================

MixDbgProfiler::MixDbgProfiler() : m_refCount(1), m_pInfo(nullptr),
    m_hPipe(INVALID_HANDLE_VALUE), m_hCmdPipe(INVALID_HANDLE_VALUE),
    m_hAckEvent(nullptr),
    m_watchCount(0), m_watchAssemblyCount(0) {
    memset(m_watchEntries, 0, sizeof(m_watchEntries));
    memset(m_watchAssemblies, 0, sizeof(m_watchAssemblies));
    InitializeCriticalSection(&m_pipeLock);
    InitializeCriticalSection(&m_watchLock);
    InitializeCriticalSection(&m_funcLock);
}

MixDbgProfiler::~MixDbgProfiler() {
    delete m_pInfo;
    if (m_hPipe != INVALID_HANDLE_VALUE)
        CloseHandle(m_hPipe);
    if (m_hCmdPipe != INVALID_HANDLE_VALUE)
        CloseHandle(m_hCmdPipe);
    if (m_hAckEvent) CloseHandle(m_hAckEvent);
    DeleteCriticalSection(&m_pipeLock);
    DeleteCriticalSection(&m_watchLock);
    DeleteCriticalSection(&m_funcLock);
}

// ============================================================================
// IUnknown
// ============================================================================

HRESULT STDMETHODCALLTYPE MixDbgProfiler::QueryInterface(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown ||
        riid == IID_ICorProfilerCallback ||
        riid == IID_ICorProfilerCallback2) {
        *ppv = static_cast<IUnknown*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

ULONG STDMETHODCALLTYPE MixDbgProfiler::AddRef() {
    return InterlockedIncrement(&m_refCount);
}

ULONG STDMETHODCALLTYPE MixDbgProfiler::Release() {
    LONG ref = InterlockedDecrement(&m_refCount);
    if (ref == 0) delete this;
    return (ULONG)ref;
}

// ============================================================================
// Private helpers
// ============================================================================

void MixDbgProfiler::WriteToPipe(const char* data, int len) {
    EnterCriticalSection(&m_pipeLock);
    if (m_hPipe != INVALID_HANDLE_VALUE) {
        DWORD written;
        WriteFile(m_hPipe, data, (DWORD)len, &written, nullptr);
    }
    LeaveCriticalSection(&m_pipeLock);
}

// ============================================================================
// Non-virtual public methods (accessed by static callbacks)
// ============================================================================

bool MixDbgProfiler::IsWatchedMethod(const char* asmName, unsigned int token) {
    EnterCriticalSection(&m_watchLock);
    bool found = false;
    for (int i = 0; i < m_watchCount; i++) {
        if (m_watchEntries[i].token == token &&
            _stricmp(m_watchEntries[i].assembly, asmName) == 0) {
            found = true;
            break;
        }
    }
    LeaveCriticalSection(&m_watchLock);
    return found;
}

bool MixDbgProfiler::IsWatchedAssembly(const char* asmName) {
    for (int i = 0; i < m_watchAssemblyCount; i++) {
        if (_stricmp(m_watchAssemblies[i], asmName) == 0)
            return true;
    }
    return false;
}

void MixDbgProfiler::ExtractAssemblyName(const WCHAR* modulePath, char* out, int outSize) {
    const WCHAR* fileName = modulePath;
    for (const WCHAR* p = modulePath; *p; p++)
        if (*p == L'\\' || *p == L'/') fileName = p + 1;
    WCHAR asmW[256] = {};
    wcsncpy_s(asmW, 256, fileName, _TRUNCATE);
    WCHAR* dot = wcsrchr(asmW, L'.');
    if (dot) *dot = L'\0';
    WideCharToMultiByte(CP_UTF8, 0, asmW, -1, out, outSize, nullptr, nullptr);
}

void MixDbgProfiler::RegisterWatchedFunction(FunctionID funcId, const FunctionWatchInfo& info) {
    EnterCriticalSection(&m_funcLock);
    for (int i = 0; i < MAX_WATCHED_FUNCS; i++) {
        if (!m_funcSlots[i].used) {
            m_funcSlots[i].id = funcId;
            m_funcSlots[i].info = info;
            m_funcSlots[i].used = true;
            break;
        }
    }
    LeaveCriticalSection(&m_funcLock);
}

const MixDbgProfiler::FunctionWatchInfo* MixDbgProfiler::FindWatchedFunction(FunctionID funcId) {
    for (int i = 0; i < MAX_WATCHED_FUNCS; i++) {
        if (m_funcSlots[i].used && m_funcSlots[i].id == funcId)
            return &m_funcSlots[i].info;
    }
    return nullptr;
}

void MixDbgProfiler::OnFunctionEnter(FunctionID funcId) {
    auto* info = FindWatchedFunction(funcId);
    if (!info || m_hPipe == INVALID_HANDLE_VALUE || !m_pInfo) return;

    UINT_PTR codeAddr = info->codeStart;
    if (codeAddr == 0) {
        LPCBYTE start = nullptr; ULONG size = 0;
        if (SUCCEEDED(m_pInfo->GetCodeInfo(funcId, &start, &size)) && start)
            codeAddr = (UINT_PTR)start;
    }
    if (codeAddr == 0) return;

    // Get the native offset of the actual method body (past any hook preamble).
    ULONG32 bodyOffset = m_pInfo->GetMethodBodyOffset(funcId);
    UINT_PTR bodyAddr = codeAddr + bodyOffset;

    // Send ENTER notification. MixDbg tracks activation count:
    //   - count 0→1 (first entry): installs HW BP at the method body, then ACKs.
    //   - count > 0 (nested/recursive): ACKs immediately.
    // We always block on ACK so first-entry is synchronous; recursive entries
    // return immediately because MixDbg signals without waiting.
    DWORD osThreadId = GetCurrentThreadId();
    char line[512];
    int len = sprintf_s(line, sizeof(line), "ENTER:%08X:%016llX:%08X:%s\n",
        (unsigned int)info->token, (unsigned long long)bodyAddr, osThreadId, info->assembly);
    if (len > 0) {
        WriteToPipe(line, len);
        if (m_hAckEvent) WaitForSingleObject(m_hAckEvent, 500);
    }
}

void MixDbgProfiler::OnFunctionLeave(FunctionID funcId) {
    auto* info = FindWatchedFunction(funcId);
    if (!info || m_hPipe == INVALID_HANDLE_VALUE) return;

    DWORD osThreadId = GetCurrentThreadId();
    char line[512];
    int len = sprintf_s(line, sizeof(line), "LEAVE:%08X:%08X:%s\n",
        (unsigned int)info->token, osThreadId, info->assembly);
    if (len > 0) {
        // Fire-and-forget: MixDbg decrements the activation count and,
        // when it reaches 0, removes the HW BP. No ACK required.
        WriteToPipe(line, len);
    }
}

// JITInlining — disables inlining when the callee is watched. Without this,
// the JIT can inline the callee body into the caller, and FunctionEnter/Leave
// hooks will never fire for the inlined copy.
HRESULT STDMETHODCALLTYPE MixDbgProfiler::JITInlining(
    FunctionID /*callerId*/, FunctionID calleeId, BOOL* pfShouldInline)
{
    if (!pfShouldInline) return S_OK;
    *pfShouldInline = TRUE;

    if (!m_pInfo) return S_OK;

    ClassID classId = 0; ModuleID moduleId = 0; mdToken token = 0;
    if (FAILED(m_pInfo->GetFunctionInfo(calleeId, &classId, &moduleId, &token)))
        return S_OK;

    WCHAR modulePath[512] = {}; ULONG pathLen = 0; AssemblyID asmId = 0; LPCBYTE baseAddr = nullptr;
    if (FAILED(m_pInfo->GetModuleInfo(moduleId, &baseAddr, 512, &pathLen, modulePath, &asmId)))
        return S_OK;
    if (modulePath[0] == L'\0') return S_OK;

    char asmUtf8[256] = {};
    ExtractAssemblyName(modulePath, asmUtf8, sizeof(asmUtf8));

    if (IsWatchedMethod(asmUtf8, token) || IsWatchedAssembly(asmUtf8))
        *pfShouldInline = FALSE;

    return S_OK;
}

// ============================================================================
// Command pipe reader — receives WATCH commands from MixDbg at runtime
// ============================================================================

void MixDbgProfiler::CmdReaderLoop() {
    if (m_hCmdPipe == INVALID_HANDLE_VALUE) return;

    char buf[4096];
    int bufLen = 0;

    while (m_hCmdPipe != INVALID_HANDLE_VALUE) {
        DWORD bytesRead = 0;
        if (!ReadFile(m_hCmdPipe, buf + bufLen, sizeof(buf) - bufLen - 1, &bytesRead, nullptr) || bytesRead == 0)
            break;

        bufLen += (int)bytesRead;
        buf[bufLen] = '\0';

        // Process complete lines (newline-terminated).
        char* start = buf;
        char* nl;
        while ((nl = strchr(start, '\n')) != nullptr) {
            *nl = '\0';

            // Parse "WATCH:Assembly:TokenHex"
            if (strncmp(start, "WATCH:", 6) == 0) {
                char* payload = start + 6;
                // Strip trailing \r from CRLF line endings.
                char* cr = strchr(payload, '\r');
                if (cr) *cr = '\0';
                char* colon = strchr(payload, ':');
                if (colon) {
                    *colon = '\0';
                    unsigned int token = strtoul(colon + 1, nullptr, 16);
                    EnterCriticalSection(&m_watchLock);
                    if (m_watchCount < MAX_WATCH) {
                        strncpy_s(m_watchEntries[m_watchCount].assembly, 256, payload, _TRUNCATE);
                        m_watchEntries[m_watchCount].token = token;
                        m_watchCount++;
                    }
                    LeaveCriticalSection(&m_watchLock);

                    // Ensure enter/leave hooks are set up (may be first watch token
                    // if no pre-launch BPs existed — e.g. late-loaded assemblies).
                    if (!m_hooksActive && m_pInfo) {
                        // First dynamic watch: set up global profiler pointer and
                        // FunctionIDMapper so the CLR consults us for every new JIT.
                        if (!g_pProfiler) {
                            g_pProfiler = this;
                            m_pInfo->SetFunctionIDMapper((void*)&MixDbgFunctionIDMapper);
                        }
                        HRESULT hr = m_pInfo->SetEnterLeaveFunctionHooks(
                            (void*)FunctionEnterNaked, (void*)FunctionLeaveNaked, (void*)FunctionTailcallNaked);
                        if (SUCCEEDED(hr)) {
                            m_pInfo->SetEventMask(
                                COR_PRF_MONITOR_JIT_COMPILATION | COR_PRF_MONITOR_ENTERLEAVE);
                            m_hooksActive = true;
                        }
                    }
                }
            }

            start = nl + 1;
        }

        // Move any remaining partial line to the beginning of the buffer.
        bufLen = (int)(buf + bufLen - start);
        if (bufLen > 0 && start != buf)
            memmove(buf, start, bufLen);
    }
}

// ============================================================================
// ICorProfilerCallback — Initialize
// ============================================================================

HRESULT STDMETHODCALLTYPE MixDbgProfiler::Initialize(IUnknown* pICorProfilerInfoUnk) {
    // QI for ICorProfilerInfo to get function/module/code information.
    IUnknown* pInfo = nullptr;
    HRESULT hr = pICorProfilerInfoUnk->QueryInterface(IID_ICorProfilerInfo, (void**)&pInfo);
    if (FAILED(hr))
        return E_FAIL;

    m_pInfo = new ProfilerInfo(pInfo);

    // Request JIT notifications. If watch tokens exist, also request enter/leave hooks.
    DWORD eventMask = COR_PRF_MONITOR_JIT_COMPILATION;

    // Read pipe name from env var set by MixDbg before CreateProcess.
    WCHAR pipeName[256] = {};
    DWORD len = GetEnvironmentVariableW(L"MIXDBG_PIPE_NAME", pipeName, 256);
    if (len == 0 || len >= 256)
        return E_FAIL;

    // Connect to MixDbg's named pipe server.
    m_hPipe = CreateFileW(
        pipeName,
        GENERIC_WRITE,
        0,          // no sharing
        nullptr,    // default security
        OPEN_EXISTING,
        0,          // default attributes
        nullptr);

    if (m_hPipe == INVALID_HANDLE_VALUE)
        return E_FAIL;

    // Open the ACK event that MixDbg signals after processing each ENTER notification.
    // The profiler blocks on this event in OnFunctionEnter so that the hardware
    // breakpoint is installed on first entry (count 0→1) before the method body
    // runs. For recursive/nested entries, MixDbg signals the ACK immediately.
    WCHAR ackEventName[256] = {};
    len = GetEnvironmentVariableW(L"MIXDBG_ACK_EVENT", ackEventName, 256);
    if (len > 0 && len < 256)
        m_hAckEvent = OpenEventW(SYNCHRONIZE, FALSE, ackEventName);

    // Connect to MixDbg's command pipe for receiving dynamic WATCH commands.
    WCHAR cmdPipeName[256] = {};
    len = GetEnvironmentVariableW(L"MIXDBG_CMD_PIPE", cmdPipeName, 256);
    if (len > 0 && len < 256) {
        m_hCmdPipe = CreateFileW(
            cmdPipeName,
            GENERIC_READ,
            0,          // no sharing
            nullptr,    // default security
            OPEN_EXISTING,
            0,          // default attributes
            nullptr);

        if (m_hCmdPipe != INVALID_HANDLE_VALUE) {
            // Start a reader thread for WATCH commands.
            CreateThread(nullptr, 0, [](LPVOID param) -> DWORD {
                auto* self = (MixDbgProfiler*)param;
                self->CmdReaderLoop();
                return 0;
            }, this, 0, nullptr);
        }
    }

    // Parse "ASSEMBLY:TOKEN,ASSEMBLY:TOKEN,..." — exact methods to block on.
    // Only these methods wait for MixDbg to set the hardware BP before executing.
    // All other JITs (framework, runtime) pass through without blocking.
    char watchBuf[2048] = {};
    DWORD watchLen = GetEnvironmentVariableA("MIXDBG_WATCH_TOKENS", watchBuf, sizeof(watchBuf));
    if (watchLen > 0 && watchLen < sizeof(watchBuf)) {
        char* ctx = nullptr;
        char* tok = strtok_s(watchBuf, ",", &ctx);
        while (tok && m_watchCount < MAX_WATCH) {
            // Parse "Assembly:HexToken"
            char* colon = strchr(tok, ':');
            if (colon) {
                *colon = '\0';
                strncpy_s(m_watchEntries[m_watchCount].assembly, 256, tok, _TRUNCATE);
                m_watchEntries[m_watchCount].token = strtoul(colon + 1, nullptr, 16);
                m_watchCount++;
            }
            tok = strtok_s(nullptr, ",", &ctx);
        }
    }

    // Parse "Asm1,Asm2,..." — assemblies to watch at assembly level (C++/CLI).
    // FunctionIDMapper hooks every method from these assemblies.
    char asmBuf[2048] = {};
    DWORD asmLen = GetEnvironmentVariableA("MIXDBG_WATCH_ASSEMBLIES", asmBuf, sizeof(asmBuf));
    if (asmLen > 0 && asmLen < sizeof(asmBuf)) {
        char* ctx = nullptr;
        char* tok = strtok_s(asmBuf, ",", &ctx);
        while (tok && m_watchAssemblyCount < MAX_WATCH_ASM) {
            strncpy_s(m_watchAssemblies[m_watchAssemblyCount], 256, tok, _TRUNCATE);
            m_watchAssemblyCount++;
            tok = strtok_s(nullptr, ",", &ctx);
        }
    }

    if (m_watchCount > 0 || m_watchAssemblyCount > 0) {
        g_pProfiler = this;
        m_pInfo->SetFunctionIDMapper((void*)&MixDbgFunctionIDMapper);
        hr = m_pInfo->SetEnterLeaveFunctionHooks(
            (void*)FunctionEnterNaked, (void*)FunctionLeaveNaked, (void*)FunctionTailcallNaked);
        if (SUCCEEDED(hr)) {
            eventMask |= COR_PRF_MONITOR_ENTERLEAVE;
            m_hooksActive = true;
        }
    }

    hr = m_pInfo->SetEventMask(eventMask);
    if (FAILED(hr))
        return E_FAIL;

    return S_OK;
}

// ============================================================================
// ICorProfilerCallback — Shutdown
// ============================================================================

HRESULT STDMETHODCALLTYPE MixDbgProfiler::Shutdown() {
    EnterCriticalSection(&m_pipeLock);
    if (m_hPipe != INVALID_HANDLE_VALUE) {
        CloseHandle(m_hPipe);
        m_hPipe = INVALID_HANDLE_VALUE;
    }
    LeaveCriticalSection(&m_pipeLock);
    return S_OK;
}

// ============================================================================
// ICorProfilerCallback — JITCompilationFinished
// ============================================================================

HRESULT STDMETHODCALLTYPE MixDbgProfiler::JITCompilationFinished(
    FunctionID functionId, HRESULT hrStatus, BOOL /*fIsSafeToBlock*/)
{
    // Skip failed JIT compilations or if pipe/info not available.
    if (FAILED(hrStatus) || m_hPipe == INVALID_HANDLE_VALUE || !m_pInfo)
        return S_OK;

    // Get metadata token and module ID.
    ClassID classId = 0;
    ModuleID moduleId = 0;
    mdToken token = 0;
    if (FAILED(m_pInfo->GetFunctionInfo(functionId, &classId, &moduleId, &token)))
        return S_OK;

    // Get native code start address.
    LPCBYTE codeStart = nullptr;
    ULONG codeSize = 0;
    if (FAILED(m_pInfo->GetCodeInfo(functionId, &codeStart, &codeSize)))
        return S_OK;
    if (!codeStart)
        return S_OK;

    // Get module file path (to extract assembly name).
    WCHAR modulePath[512] = {};
    ULONG pathLen = 0;
    AssemblyID assemblyId = 0;
    LPCBYTE baseAddr = nullptr;
    if (FAILED(m_pInfo->GetModuleInfo(moduleId, &baseAddr, 512, &pathLen, modulePath, &assemblyId)))
        return S_OK;

    // Skip dynamic/in-memory modules (no file path).
    if (modulePath[0] == L'\0')
        return S_OK;

    // Extract assembly name: strip directory and extension.
    // "C:\...\WpfApp.dll" -> "WpfApp"
    char asmUtf8[256] = {};
    ExtractAssemblyName(modulePath, asmUtf8, sizeof(asmUtf8));

    char line[4096]; // Large enough for IL-to-native mapping entries.
    int lineLen;

    if (m_hooksActive) {
        // With enter/leave hooks: send prefixed JIT notification.
        // For watched methods, include IL-to-native mapping so MixDbg can set
        // hardware BPs at exact source lines (not just method entry).
        lineLen = sprintf_s(line, sizeof(line), "JIT:%08X:%016llX:%08X:%s",
            (unsigned int)token, (unsigned long long)(UINT_PTR)codeStart,
            (unsigned int)codeSize, asmUtf8);

        if (lineLen > 0) {
            // Append IL-to-native mapping for exact-line BP resolution: :IL0=N0,IL1=N1,...
            ILNativeMap maps[128];
            ULONG32 mapCount = 0;
            if (SUCCEEDED(m_pInfo->GetILToNativeMapping(functionId, 128, &mapCount, maps)) && mapCount > 0) {
                lineLen += sprintf_s(line + lineLen, sizeof(line) - lineLen, ":");
                for (ULONG32 i = 0; i < mapCount && i < 128; i++) {
                    if ((int)maps[i].ilOffset < 0) continue; // skip prolog/epilog markers
                    if (lineLen > 1 && line[lineLen-1] != ':')
                        lineLen += sprintf_s(line + lineLen, sizeof(line) - lineLen, ",");
                    lineLen += sprintf_s(line + lineLen, sizeof(line) - lineLen,
                        "%X=%X", maps[i].ilOffset, maps[i].nativeStartOffset);
                }
            }
        }

        if (lineLen > 0) {
            lineLen += sprintf_s(line + lineLen, sizeof(line) - lineLen, "\n");
            WriteToPipe(line, lineLen);
        }
    } else {
        // Without hooks: use old format with blocking for watched methods.
        lineLen = sprintf_s(line, sizeof(line), "%08X:%016llX:%08X:%s\n",
            (unsigned int)token, (unsigned long long)(UINT_PTR)codeStart,
            (unsigned int)codeSize, asmUtf8);
        if (lineLen > 0) {
            WriteToPipe(line, lineLen);
            if (m_hAckEvent && m_watchCount > 0 && IsWatchedMethod(asmUtf8, token))
                WaitForSingleObject(m_hAckEvent, 500);
        }
    }

    return S_OK;
}
