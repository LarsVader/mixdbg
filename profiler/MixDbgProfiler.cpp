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
    m_watchCount(0) {
    memset(m_watchEntries, 0, sizeof(m_watchEntries));
    InitializeCriticalSection(&m_pipeLock);
    InitializeCriticalSection(&m_watchLock);
    InitializeCriticalSection(&m_funcLock);
}

MixDbgProfiler::~MixDbgProfiler() {
    // Clear the global pointer FIRST so MixDbgHelper exports (which check
    // `if (!g_pProfiler) return;`) start no-op'ing immediately. Otherwise a
    // stale rejitted method body in attach mode could call into a destroyed
    // profiler instance and dereference freed memory.
    if (g_pProfiler == this) g_pProfiler = nullptr;

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
    // Note: ICorProfilerCallback3/4 are declared as extra virtuals on the same
    // class — the vtable layout starts with all ICorProfilerCallback methods,
    // then 2, then 3, then 4. A single `this` pointer is valid for any of them.
    if (riid == IID_IUnknown ||
        riid == IID_ICorProfilerCallback ||
        riid == IID_ICorProfilerCallback2 ||
        riid == IID_ICorProfilerCallback3 ||
        riid == IID_ICorProfilerCallback4) {
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

    // Only block inlining for methods with exact WATCH tokens (breakpoints).
    if (IsWatchedMethod(asmUtf8, token))
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
                    bool added = false;
                    if (m_watchCount < MAX_WATCH) {
                        strncpy_s(m_watchEntries[m_watchCount].assembly, 256, payload, _TRUNCATE);
                        m_watchEntries[m_watchCount].token = token;
                        m_watchCount++;
                        added = true;
                    }
                    LeaveCriticalSection(&m_watchLock);

                    // Surface watch-list overflow to MixDbg — silently
                    // dropping watches makes BPs invisibly fail to bind.
                    if (!added) {
                        char warn[256];
                        int wlen = sprintf_s(warn, sizeof(warn),
                            "READY:watch-overflow:%s:%08X (MAX_WATCH=%d reached)\n",
                            payload, token, MAX_WATCH);
                        if (wlen > 0) WriteToPipe(warn, wlen);
                    }

                    // Attach mode: ReJIT integration is M9 work — the current
                    // implementation relies on the MixDbg-side eager-install +
                    // DAC fallback (ManagedBreakpointResolverService) to bind
                    // HW BPs at the IL-mapped native address for already-JIT'd
                    // and newly-JIT'd methods, capped at 4 concurrent BPs. When
                    // M9 lands, this is the entry point to call
                    // ICorProfilerInfo4::RequestReJIT(0, 1, &moduleId, &token)
                    // — the per-module ModuleID cache must be seeded from
                    // JITCompilationFinished + EnumModules during
                    // InitializeForAttach.
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

// Connect to the named notification pipe. Returns false on failure.
static HANDLE ConnectNotificationPipe(const WCHAR* pipeName) {
    return CreateFileW(pipeName, GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
}

// Connect to the command pipe (read-only). Returns INVALID_HANDLE_VALUE on failure.
static HANDLE ConnectCommandPipe(const WCHAR* pipeName) {
    return CreateFileW(pipeName, GENERIC_READ, 0, nullptr, OPEN_EXISTING, 0, nullptr);
}


HRESULT STDMETHODCALLTYPE MixDbgProfiler::Initialize(IUnknown* pICorProfilerInfoUnk) {
    // Validate env vars and connect the notification pipe BEFORE allocating
    // m_pInfo — the runtime won't call Shutdown on a profiler that returns
    // failure from Initialize, so any allocation between QI and an early
    // return leaks for the lifetime of the target process. Same pattern as
    // InitializeForAttach (validate-before-allocate).
    WCHAR pipeName[256] = {};
    DWORD len = GetEnvironmentVariableW(L"MIXDBG_PIPE_NAME", pipeName, 256);
    if (len == 0 || len >= 256)
        return E_FAIL;

    HANDLE hPipe = ConnectNotificationPipe(pipeName);
    if (hPipe == INVALID_HANDLE_VALUE)
        return E_FAIL;

    // QI for ICorProfilerInfo to get function/module/code information.
    IUnknown* pInfo = nullptr;
    HRESULT hr = pICorProfilerInfoUnk->QueryInterface(IID_ICorProfilerInfo, (void**)&pInfo);
    if (FAILED(hr)) {
        CloseHandle(hPipe);
        return E_FAIL;
    }

    m_pInfo = new ProfilerInfo(pInfo);
    m_hPipe = hPipe;

    // Request JIT notifications. If watch tokens exist, also request enter/leave hooks.
    DWORD eventMask = COR_PRF_MONITOR_JIT_COMPILATION;

    // Open the ACK event that MixDbg signals after processing each ENTER notification.
    // The profiler blocks on this event in OnFunctionEnter so that the hardware
    // breakpoint is installed on first entry (count 0→1) before the method body
    // runs. For recursive/nested entries, MixDbg signals the ACK immediately.
    WCHAR ackEventName[256] = {};
    len = GetEnvironmentVariableW(L"MIXDBG_ACK_EVENT", ackEventName, 256);
    if (len > 0 && len < 256)
        m_hAckEvent = OpenEventW(SYNCHRONIZE, FALSE, ackEventName);

    // Connect to MixDbg's command pipe for receiving dynamic WATCH commands.
    // Cmd-reader thread is spawned later, after SetEventMask succeeds —
    // spawning it here would race with the SetEventMask-failure cleanup
    // path (handle close + destructor) and produce a use-after-free on
    // m_watchEntries / m_watchLock. Same shape as InitializeForAttach.
    WCHAR cmdPipeName[256] = {};
    len = GetEnvironmentVariableW(L"MIXDBG_CMD_PIPE", cmdPipeName, 256);
    if (len > 0 && len < 256)
        m_hCmdPipe = ConnectCommandPipe(cmdPipeName);

    // Parse "ASSEMBLY:TOKEN,ASSEMBLY:TOKEN,..." — exact methods to block on.
    // Only these methods wait for MixDbg to set the hardware BP before executing.
    // All other JITs (framework, runtime) pass through without blocking.
    char watchBuf[2048] = {};
    DWORD watchLen = GetEnvironmentVariableA("MIXDBG_WATCH_TOKENS", watchBuf, sizeof(watchBuf));
    if (watchLen > 0 && watchLen < sizeof(watchBuf)) {
        char* ctx = nullptr;
        char* tok = strtok_s(watchBuf, ",", &ctx);
        EnterCriticalSection(&m_watchLock);
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
        LeaveCriticalSection(&m_watchLock);
    }

    // Always activate hooks — mid-session WATCH commands can arrive at any time
    // via the command pipe. FunctionIDMapper returns FALSE for non-watched methods
    // so only exact-token matches get ENTER/LEAVE overhead.
    g_pProfiler = this;
    m_pInfo->SetFunctionIDMapper((void*)&MixDbgFunctionIDMapper);
    hr = m_pInfo->SetEnterLeaveFunctionHooks(
        (void*)FunctionEnterNaked, (void*)FunctionLeaveNaked, (void*)FunctionTailcallNaked);
    if (SUCCEEDED(hr)) {
        eventMask |= COR_PRF_MONITOR_ENTERLEAVE;
        m_hooksActive = true;
    }

    hr = m_pInfo->SetEventMask(eventMask);
    if (FAILED(hr)) {
        // Same logic as InitializeForAttach: the runtime won't call Shutdown
        // after we report failure here, so explicitly release the resources
        // we've allocated. Cmd-reader thread isn't spawned yet (intentionally
        // deferred to after this point) so closing m_hCmdPipe is safe.
        if (m_hPipe != INVALID_HANDLE_VALUE) { CloseHandle(m_hPipe); m_hPipe = INVALID_HANDLE_VALUE; }
        if (m_hAckEvent) { CloseHandle(m_hAckEvent); m_hAckEvent = nullptr; }
        if (m_hCmdPipe != INVALID_HANDLE_VALUE) { CloseHandle(m_hCmdPipe); m_hCmdPipe = INVALID_HANDLE_VALUE; }
        delete m_pInfo;
        m_pInfo = nullptr;
        g_pProfiler = nullptr;
        return E_FAIL;
    }

    // Spawn the cmd-reader now that init has succeeded — see the deferral
    // comment above where m_hCmdPipe is opened. This mirrors the pattern
    // in InitializeForAttach.
    if (m_hCmdPipe != INVALID_HANDLE_VALUE) {
        CreateThread(nullptr, 0, [](LPVOID param) -> DWORD {
            auto* self = (MixDbgProfiler*)param;
            self->CmdReaderLoop();
            return 0;
        }, this, 0, nullptr);
    }

    return S_OK;
}

// ============================================================================
// ICorProfilerCallback3 — InitializeForAttach
// ============================================================================
//
// Called by the CLR when the profiler is loaded via the diagnostic IPC
// AttachProfiler command (instead of CORECLR_PROFILER env vars). Configuration
// values that env vars carry in the launch path arrive here as a binary blob.
//
// Two CLR restrictions force a different setup than launch:
//   * COR_PRF_MONITOR_ENTERLEAVE is NOT in COR_PRF_ALLOWABLE_AFTER_ATTACH —
//     SetEnterLeaveFunctionHooks would fail and SetEventMask would reject the
//     flag. Method entry/exit must be observed via IL rewriting (ReJIT) that
//     calls into the helper exports (MixDbgHelper_Enter / MixDbgHelper_Leave).
//   * COR_PRF_ENABLE_REJIT IS allowed at attach in CoreCLR (see
//     dotnet/coreclr#19054). It enables RequestReJIT/RequestReJITWithInliners.

// Read a uint32 little-endian from a byte buffer at offset, advancing pos.
static UINT32 ReadU32LE(const BYTE* data, UINT cb, UINT& pos) {
    if (pos + 4 > cb) return 0;
    UINT32 v = (UINT32)data[pos]
             | ((UINT32)data[pos + 1] << 8)
             | ((UINT32)data[pos + 2] << 16)
             | ((UINT32)data[pos + 3] << 24);
    pos += 4;
    return v;
}

static UINT16 ReadU16LE(const BYTE* data, UINT cb, UINT& pos) {
    if (pos + 2 > cb) return 0;
    UINT16 v = (UINT16)data[pos] | ((UINT16)data[pos + 1] << 8);
    pos += 2;
    return v;
}

// Read a UTF-16LE length-prefixed string into a WCHAR buffer (null-terminated).
static bool ReadUtf16Str(const BYTE* data, UINT cb, UINT& pos, WCHAR* out, int outChars) {
    UINT16 len = ReadU16LE(data, cb, pos);
    if (len >= outChars) return false;
    if (pos + (UINT)len * 2 > cb) return false;
    for (UINT16 i = 0; i < len; i++) {
        out[i] = (WCHAR)((UINT16)data[pos] | ((UINT16)data[pos + 1] << 8));
        pos += 2;
    }
    out[len] = L'\0';
    return true;
}

HRESULT STDMETHODCALLTYPE MixDbgProfiler::InitializeForAttach(
    IUnknown* pCorProfilerInfoUnk, void* pvClientData, UINT cbClientData)
{
    // Validate the blob BEFORE any allocation — the runtime won't call
    // Shutdown on a profiler that returned a failure HRESULT here, so any
    // resources allocated before an early return leak for the lifetime of
    // the target process.
    if (!pvClientData || cbClientData < 4) return E_INVALIDARG;
    const BYTE* data = (const BYTE*)pvClientData;
    UINT pos = 0;
    UINT32 version = ReadU32LE(data, cbClientData, pos);
    if (version != 1) return E_INVALIDARG;

    WCHAR pipeName[256] = {};
    WCHAR ackName[256] = {};
    WCHAR cmdName[256] = {};
    if (!ReadUtf16Str(data, cbClientData, pos, pipeName, 256)) return E_INVALIDARG;
    if (!ReadUtf16Str(data, cbClientData, pos, ackName, 256)) return E_INVALIDARG;
    if (!ReadUtf16Str(data, cbClientData, pos, cmdName, 256)) return E_INVALIDARG;

    IUnknown* pInfo = nullptr;
    HRESULT hr = pCorProfilerInfoUnk->QueryInterface(IID_ICorProfilerInfo, (void**)&pInfo);
    if (FAILED(hr))
        return E_FAIL;

    m_pInfo = new ProfilerInfo(pInfo);
    m_isAttachMode = true;
    g_pProfiler = this;

    m_hPipe = ConnectNotificationPipe(pipeName);
    if (m_hPipe == INVALID_HANDLE_VALUE) {
        // Past this point the runtime treats us as failed-to-init and
        // never calls Shutdown — clean up the bits we've allocated.
        delete m_pInfo;
        m_pInfo = nullptr;
        g_pProfiler = nullptr;
        return E_FAIL;
    }

    if (ackName[0])
        m_hAckEvent = OpenEventW(SYNCHRONIZE, FALSE, ackName);

    if (cmdName[0]) {
        m_hCmdPipe = ConnectCommandPipe(cmdName);
        // Note: cmd-reader thread is spawned AFTER SetEventMask succeeds
        // (below). Spawning here would race with the SetEventMask failure
        // cleanup path — closing m_hCmdPipe doesn't synchronously join the
        // reader, so the reader could touch m_watchEntries / m_watchLock
        // after the destructor releases them.
    }

    UINT32 watchCount = ReadU32LE(data, cbClientData, pos);
    UINT32 dropped = 0;
    char droppedFirstAsm[256] = {};
    UINT32 droppedFirstToken = 0;
    if (watchCount > 0) {
        EnterCriticalSection(&m_watchLock);
        for (UINT32 i = 0; i < watchCount; i++) {
            UINT16 asmLen = ReadU16LE(data, cbClientData, pos);
            if (asmLen >= 256 || pos + asmLen > cbClientData) break;
            char asmName[256] = {};
            for (UINT16 j = 0; j < asmLen; j++) asmName[j] = (char)data[pos + j];
            pos += asmLen;
            UINT32 token = ReadU32LE(data, cbClientData, pos);
            if (m_watchCount < MAX_WATCH) {
                strncpy_s(m_watchEntries[m_watchCount].assembly, 256, asmName, _TRUNCATE);
                m_watchEntries[m_watchCount].token = token;
                m_watchCount++;
            } else {
                if (dropped == 0) {
                    strncpy_s(droppedFirstAsm, 256, asmName, _TRUNCATE);
                    droppedFirstToken = token;
                }
                dropped++;
            }
        }
        LeaveCriticalSection(&m_watchLock);
    }

    // Attach mode cannot use ENTERLEAVE hooks — synthesize via IL rewriting
    // through GetReJITParameters and the MixDbgHelper exports. The
    // m_hooksActive flag is overloaded — it gates the prefixed wire format
    // in JITCompilationFinished (JIT:token:addr:size:asm[:IL-map]) AND
    // historically meant "runtime ENTER/LEAVE hooks are armed". In attach
    // mode the second meaning is false (the runtime rejects
    // COR_PRF_MONITOR_ENTERLEAVE), but we set the flag to true so the
    // wire format gate triggers — MixDbg needs the IL-map for eager-install.
    // Future cleanup: split into m_useNewWireFormat and m_runtimeEnterLeave.
    m_hooksActive = true;

    DWORD eventMask = COR_PRF_MONITOR_JIT_COMPILATION | COR_PRF_ENABLE_REJIT;
    hr = m_pInfo->SetEventMask(eventMask);
    if (FAILED(hr)) {
        // Same logic as the early returns above: the runtime won't call
        // Shutdown after we report failure here, so explicitly release the
        // resources we've allocated. Cmd-reader thread isn't spawned yet
        // (intentionally deferred to after this point) so closing
        // m_hCmdPipe is safe.
        if (m_hPipe != INVALID_HANDLE_VALUE) { CloseHandle(m_hPipe); m_hPipe = INVALID_HANDLE_VALUE; }
        if (m_hAckEvent) { CloseHandle(m_hAckEvent); m_hAckEvent = nullptr; }
        if (m_hCmdPipe != INVALID_HANDLE_VALUE) { CloseHandle(m_hCmdPipe); m_hCmdPipe = INVALID_HANDLE_VALUE; }
        delete m_pInfo;
        m_pInfo = nullptr;
        g_pProfiler = nullptr;
        return E_FAIL;
    }

    // Spawn the cmd-reader now that all init has succeeded. Even if it
    // races with subsequent code in this function (watch-overflow log,
    // READY:attach write), those writes are protected by m_pipeLock and
    // safe.
    if (m_hCmdPipe != INVALID_HANDLE_VALUE) {
        CreateThread(nullptr, 0, [](LPVOID param) -> DWORD {
            auto* self = (MixDbgProfiler*)param;
            self->CmdReaderLoop();
            return 0;
        }, this, 0, nullptr);
    }

    // Surface watch-list overflow to MixDbg — silently dropping watches
    // makes BPs invisibly fail to bind. Symmetric to the cmd-reader path
    // which logs READY:watch-overflow when WATCH: lines arrive past the cap.
    if (dropped > 0) {
        char warn[320];
        int wlen = sprintf_s(warn, sizeof(warn),
            "READY:watch-overflow:%s:%08X (MAX_WATCH=%d reached, %u entries dropped)\n",
            droppedFirstAsm, droppedFirstToken, MAX_WATCH, (unsigned)dropped);
        if (wlen > 0) WriteToPipe(warn, wlen);
    }

    // Announce readiness so the reader thread on the MixDbg side can detect
    // a successful attach apart from a launch.
    const char* ready = "READY:attach\n";
    WriteToPipe(ready, (int)strlen(ready));

    return S_OK;
}

// ============================================================================
// ICorProfilerCallback4 — GetReJITParameters
// ============================================================================
//
// Called by the CLR for every method we asked to ReJIT. The full
// implementation rewrites the method body to inject calls to the
// MixDbgHelper.Enter / MixDbgHelper.Leave P/Invokes (synthesized as new
// methoddefs in the target module's metadata) wrapped in an outer try/finally,
// so we get ENTER/LEAVE notifications equivalent to the runtime hooks
// available at launch time.
//
// This stub leaves the IL untouched — the runtime then rejits the method
// without instrumentation, which is harmless. The hardware-BP path
// (JITCompilationFinished → JIT: notification → MixDbg installs HW BP at the
// IL-mapped native address) still functions for newly JIT'd code, and the
// DAC-fallback path on the MixDbg side handles already-JIT'd methods.
HRESULT STDMETHODCALLTYPE MixDbgProfiler::GetReJITParameters(
    ModuleID /*moduleId*/, mdMethodDef /*methodId*/, IUnknown* /*pFunctionControl*/)
{
    // TODO Phase B: implement IL rewriting per the plan in docs/architecture.md
    //   1. Use IMetaDataEmit2 on the module to define a `Module.MixDbgHelper`
    //      TypeDef with two pinvoke methoddefs (Enter/Leave) pointing at
    //      `MixDbgProfiler.dll!MixDbgHelper_Enter`/`Leave` exports.
    //   2. Read the original method IL via IMetaDataImport2 + COR_ILMETHOD_DECODER.
    //   3. Allocate new IL (via ICorProfilerInfo4::GetILFunctionBodyAllocator)
    //      with: `ldc.i4 token; ldstr asm; call Enter; .try { <orig IL with
    //      ret rewritten to stloc+leave> } finally { ldc.i4 token; ldstr asm;
    //      call Leave; endfinally } NEW_RET: ldloc retLocal; ret`.
    //   4. Append a single outer SEH clause wrapping the original body so
    //      Leave fires on normal return AND exception unwind.
    //   5. Bump MaxStack by 3 and emit fat header / fat exception clauses.
    //   6. Call ICorProfilerFunctionControl::SetILFunctionBody.
    return S_OK;
}

// ============================================================================
// ICorProfilerCallback — Shutdown
// ============================================================================

HRESULT STDMETHODCALLTYPE MixDbgProfiler::Shutdown() {
    // Clear the global pointer so MixDbgHelper exports (called from rejitted
    // managed IL) start no-op'ing immediately. After Shutdown the runtime may
    // still execute methods whose IL contains injected helper P/Invokes.
    if (g_pProfiler == this) g_pProfiler = nullptr;

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

    char lineStack[4096];
    char* line = lineStack;
    size_t lineCap = sizeof(lineStack);
    int lineLen;

    if (m_hooksActive) {
        // With enter/leave hooks: send prefixed JIT notification.
        // For watched methods, include IL-to-native mapping so MixDbg can set
        // hardware BPs at exact source lines (not just method entry).
        lineLen = sprintf_s(line, lineCap, "JIT:%08X:%016llX:%08X:%s",
            (unsigned int)token, (unsigned long long)(UINT_PTR)codeStart,
            (unsigned int)codeSize, asmUtf8);

        if (lineLen > 0) {
            // Append IL-to-native mapping for exact-line BP resolution: :IL0=N0,IL1=N1,...
            // Two-pass: query count first, then allocate appropriately.
            ULONG32 mapCount = 0;
            m_pInfo->GetILToNativeMapping(functionId, 0, &mapCount, nullptr);
            if (mapCount > 0) {
                ILNativeMap* maps = (mapCount <= 128)
                    ? (ILNativeMap*)_alloca(mapCount * sizeof(ILNativeMap))
                    : (ILNativeMap*)malloc(mapCount * sizeof(ILNativeMap));
                bool heapMaps = (mapCount > 128);
                if (maps && SUCCEEDED(m_pInfo->GetILToNativeMapping(functionId, mapCount, &mapCount, maps))) {
                    // Estimate buffer: header + 20 chars per entry (worst: "XXXXXXXX=XXXXXXXX,") + newline.
                    size_t needed = (size_t)lineLen + 2 + (size_t)mapCount * 20 + 2;
                    if (needed <= sizeof(lineStack)) {
                        line = lineStack;
                    } else {
                        line = (char*)malloc(needed);
                        if (!line) { line = lineStack; goto skip_mapping; } // OOM: send JIT without mapping
                    }
                    if (line != lineStack) {
                        memcpy(line, lineStack, lineLen);
                        lineCap = needed;
                    }
                    lineLen += sprintf_s(line + lineLen, lineCap - lineLen, ":");
                    for (ULONG32 i = 0; i < mapCount; i++) {
                        if ((int)maps[i].ilOffset < 0) continue; // skip prolog/epilog markers
                        if (lineLen > 1 && line[lineLen-1] != ':')
                            lineLen += sprintf_s(line + lineLen, lineCap - lineLen, ",");
                        lineLen += sprintf_s(line + lineLen, lineCap - lineLen,
                            "%X=%X", maps[i].ilOffset, maps[i].nativeStartOffset);
                    }
                skip_mapping:;
                }
                if (heapMaps) free(maps);
            }
        }

        if (lineLen > 0) {
            lineLen += sprintf_s(line + lineLen, lineCap - lineLen, "\n");
            WriteToPipe(line, lineLen);
            if (line != lineStack) free(line);
            line = lineStack;
            lineCap = sizeof(lineStack);

            // Attach mode: ENTER/LEAVE hooks are unavailable, so the only
            // chance to install a HW BP at JIT'd code is right now, before
            // the method body executes. Block the JIT thread on the ACK
            // event for watched methods so MixDbg can install the HW BP
            // (via the eager-install path in ManagedBreakpointResolverService)
            // before the JIT returns and the method runs.
            if (m_isAttachMode && m_hAckEvent && IsWatchedMethod(asmUtf8, token))
                WaitForSingleObject(m_hAckEvent, 500);
        }
    } else {
        // Without hooks: use old format with blocking for watched methods.
        lineLen = sprintf_s(line, lineCap, "%08X:%016llX:%08X:%s\n",
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
