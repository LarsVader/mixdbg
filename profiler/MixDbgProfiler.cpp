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
    m_hPipe(INVALID_HANDLE_VALUE),
    m_hAckEvent(nullptr), m_hRehookEvent(nullptr),
    m_watchCount(0), m_watchAssemblyCount(0) {
    memset(m_watchEntries, 0, sizeof(m_watchEntries));
    memset(m_watchAssemblies, 0, sizeof(m_watchAssemblies));
    InitializeCriticalSection(&m_pipeLock);
    InitializeCriticalSection(&m_funcLock);
}

MixDbgProfiler::~MixDbgProfiler() {
    delete m_pInfo;
    if (m_hPipe != INVALID_HANDLE_VALUE)
        CloseHandle(m_hPipe);
    if (m_hAckEvent) CloseHandle(m_hAckEvent);
    if (m_hRehookEvent) CloseHandle(m_hRehookEvent);
    DeleteCriticalSection(&m_pipeLock);
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
    for (int i = 0; i < m_watchCount; i++) {
        if (m_watchEntries[i].token == token &&
            _stricmp(m_watchEntries[i].assembly, asmName) == 0)
            return true;
    }
    return false;
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
    // With enter/leave hooks, the JIT may insert a stub at the entry.
    ULONG32 bodyOffset = m_pInfo->GetMethodBodyOffset(funcId);
    UINT_PTR bodyAddr = codeAddr + bodyOffset;

    // Disable enter/leave hooks so the method body runs through the normal code path.
    m_pInfo->SetEventMask(COR_PRF_MONITOR_JIT_COMPILATION);

    DWORD osThreadId = GetCurrentThreadId();
    char line[512];
    int len = sprintf_s(line, sizeof(line), "ENTER:%08X:%016llX:%08X:%s\n",
        (unsigned int)info->token, (unsigned long long)bodyAddr, osThreadId, info->assembly);
    if (len > 0) {
        WriteToPipe(line, len);
        // Block until MixDbg sets the hardware BP.
        if (m_hAckEvent) WaitForSingleObject(m_hAckEvent, 500);
    }

    // Do NOT re-enable hooks here — the method body must run without hook
    // trampolines so the hardware BP fires. Hooks are re-enabled by the
    // rehook watcher thread when MixDbg signals after the user continues.
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

    // Open the ACK event that MixDbg signals after processing each notification.
    // The profiler blocks on this event in JITCompilationFinished so that the
    // hardware breakpoint is set before the method body executes.
    WCHAR ackEventName[256] = {};
    len = GetEnvironmentVariableW(L"MIXDBG_ACK_EVENT", ackEventName, 256);
    if (len > 0 && len < 256)
        m_hAckEvent = OpenEventW(SYNCHRONIZE, FALSE, ackEventName);

    // REHOOK event — signaled by MixDbg on Continue to re-enable enter/leave hooks.
    WCHAR rehookName[256] = {};
    len = GetEnvironmentVariableW(L"MIXDBG_REHOOK_EVENT", rehookName, 256);
    if (len > 0 && len < 256) {
        m_hRehookEvent = OpenEventW(SYNCHRONIZE, FALSE, rehookName);
        if (m_hRehookEvent) {
            // Watcher thread: waits for REHOOK signal, re-enables enter/leave hooks.
            CreateThread(nullptr, 0, [](LPVOID param) -> DWORD {
                auto* self = (MixDbgProfiler*)param;
                while (self->m_hRehookEvent && self->m_hPipe != INVALID_HANDLE_VALUE) {
                    if (WaitForSingleObject(self->m_hRehookEvent, 1000) == WAIT_OBJECT_0)
                        self->m_pInfo->SetEventMask(
                            COR_PRF_MONITOR_JIT_COMPILATION | COR_PRF_MONITOR_ENTERLEAVE);
                }
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

        if (lineLen > 0 && (IsWatchedMethod(asmUtf8, token) || IsWatchedAssembly(asmUtf8))) {
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
