// MixDbgProfiler.cpp — CLR profiler for JIT compilation notifications
//
// This DLL implements ICorProfilerCallback2. When loaded by the CLR (via
// CORECLR_ENABLE_PROFILING=1 env var), it sends JIT compilation notifications
// to MixDbg via a named pipe.
//
// Protocol: text lines over named pipe, one per JIT'd method:
//   <token_hex>:<address_hex>:<assembly_name>\n
// Example: 0600000E:00007FF7B2A01234:WpfApp\n
//
// MixDbg matches notifications against deferred managed breakpoints and sets
// hardware breakpoints (ba e1) at the reported native addresses.

#include <windows.h>
#include <unknwn.h>
#include <objbase.h>
#include <stdio.h>
#include <string.h>

// LPCBYTE is from the profiling API headers which we don't include.
typedef const BYTE* LPCBYTE;

// ============================================================================
// CoreCLR profiling type aliases (from corprof.h)
// ============================================================================
typedef UINT_PTR FunctionID;
typedef UINT_PTR ClassID;
typedef UINT_PTR ModuleID;
typedef UINT_PTR AssemblyID;
typedef UINT_PTR ThreadID;
typedef UINT_PTR ObjectID;
typedef UINT_PTR GCHandleID;
typedef UINT_PTR AppDomainID;
typedef UINT32   mdToken;

// COR_PRF_MONITOR flags
#define COR_PRF_MONITOR_JIT_COMPILATION 0x00000020
#define COR_PRF_MONITOR_ENTERLEAVE      0x00001000

// ============================================================================
// GUIDs
// ============================================================================

// MixDbgProfiler CLSID: {D13D53A1-6E42-4D6B-B4C5-8F3A7E2C1B90}
static const GUID CLSID_MixDbgProfiler =
    { 0xD13D53A1, 0x6E42, 0x4D6B, { 0xB4, 0xC5, 0x8F, 0x3A, 0x7E, 0x2C, 0x1B, 0x90 } };

// ICorProfilerCallback: {176FBED1-A55C-4796-98CA-A9DA0EF883E7}
static const GUID IID_ICorProfilerCallback =
    { 0x176FBED1, 0xA55C, 0x4796, { 0x98, 0xCA, 0xA9, 0xDA, 0x0E, 0xF8, 0x83, 0xE7 } };

// ICorProfilerCallback2: {8A8CC829-CCF2-49fe-BBAE-0F022228071A}
static const GUID IID_ICorProfilerCallback2 =
    { 0x8A8CC829, 0xCCF2, 0x49FE, { 0xBB, 0xAE, 0x0F, 0x02, 0x22, 0x28, 0x07, 0x1A } };

// ICorProfilerInfo: {28B5557D-3F3F-48b4-90B2-5F9EEA2F6C48}
static const GUID IID_ICorProfilerInfo =
    { 0x28B5557D, 0x3F3F, 0x48B4, { 0x90, 0xB2, 0x5F, 0x9E, 0xEA, 0x2F, 0x6C, 0x48 } };

// COR_DEBUG_IL_TO_NATIVE_MAP structure (3 x ULONG32)
struct ILNativeMap { ULONG32 ilOffset; ULONG32 nativeStartOffset; ULONG32 nativeEndOffset; };

// ============================================================================
// ICorProfilerInfo vtable wrapper
//
// Instead of defining the full 33-method COM interface (which requires the
// corprof.h header we don't ship), we call methods by vtable slot index.
// Slot numbers are counted from the start of the vtable (0 = QI, 1 = AddRef,
// 2 = Release, then ICorProfilerInfo methods starting at slot 3).
// ============================================================================
class ProfilerInfo {
    IUnknown* m_pInfo;
    void** m_vtbl;

public:
    explicit ProfilerInfo(IUnknown* pInfo) : m_pInfo(pInfo) {
        m_vtbl = *reinterpret_cast<void***>(pInfo);
    }

    // Slot 5: GetCodeInfo(FunctionID, LPCBYTE*, ULONG*)
    // Returns the start address and size of JIT'd native code.
    HRESULT GetCodeInfo(FunctionID funcId, LPCBYTE* pStart, ULONG* pcSize) {
        typedef HRESULT(STDMETHODCALLTYPE* Fn)(void*, FunctionID, LPCBYTE*, ULONG*);
        return reinterpret_cast<Fn>(m_vtbl[5])(m_pInfo, funcId, pStart, pcSize);
    }

    // Slot 15: GetFunctionInfo(FunctionID, ClassID*, ModuleID*, mdToken*)
    // Returns the metadata token and containing module for a function.
    HRESULT GetFunctionInfo(FunctionID funcId, ClassID* pClassId,
                            ModuleID* pModuleId, mdToken* pToken) {
        typedef HRESULT(STDMETHODCALLTYPE* Fn)(void*, FunctionID, ClassID*, ModuleID*, mdToken*);
        return reinterpret_cast<Fn>(m_vtbl[15])(m_pInfo, funcId, pClassId, pModuleId, pToken);
    }

    // Slot 16: SetEventMask(DWORD)
    // Configures which profiler events the CLR delivers.
    HRESULT SetEventMask(DWORD dwEvents) {
        typedef HRESULT(STDMETHODCALLTYPE* Fn)(void*, DWORD);
        return reinterpret_cast<Fn>(m_vtbl[16])(m_pInfo, dwEvents);
    }

    // Slot 17: SetEnterLeaveFunctionHooks(FunctionEnter*, FunctionLeave*, FunctionTailcall*)
    HRESULT SetEnterLeaveFunctionHooks(void* pEnter, void* pLeave, void* pTailcall) {
        typedef HRESULT(STDMETHODCALLTYPE* Fn)(void*, void*, void*, void*);
        return reinterpret_cast<Fn>(m_vtbl[17])(m_pInfo, pEnter, pLeave, pTailcall);
    }

    // Slot 18: SetFunctionIDMapper(FunctionIDMapper*)
    HRESULT SetFunctionIDMapper(void* pFunc) {
        typedef HRESULT(STDMETHODCALLTYPE* Fn)(void*, void*);
        return reinterpret_cast<Fn>(m_vtbl[18])(m_pInfo, pFunc);
    }

    // Slot 20: GetModuleInfo(ModuleID, LPCBYTE*, ULONG, ULONG*, WCHAR*, AssemblyID*)
    HRESULT GetModuleInfo(ModuleID moduleId, LPCBYTE* ppBaseAddr, ULONG cchName,
                          ULONG* pcchName, WCHAR* szName, AssemblyID* pAssemblyId) {
        typedef HRESULT(STDMETHODCALLTYPE* Fn)(void*, ModuleID, LPCBYTE*, ULONG, ULONG*, WCHAR*, AssemblyID*);
        return reinterpret_cast<Fn>(m_vtbl[20])(m_pInfo, moduleId, ppBaseAddr, cchName, pcchName, szName, pAssemblyId);
    }

    // Slot 35: GetILToNativeMapping(FunctionID, ULONG32, ULONG32*, ILNativeMap[])
    HRESULT GetILToNativeMapping(FunctionID funcId, ULONG32 cMap, ULONG32* pcMap, void* map) {
        typedef HRESULT(STDMETHODCALLTYPE* Fn)(void*, FunctionID, ULONG32, ULONG32*, void*);
        return reinterpret_cast<Fn>(m_vtbl[35])(m_pInfo, funcId, cMap, pcMap, map);
    }

    // Returns the native offset for the first IL instruction (method body start).
    ULONG32 GetMethodBodyOffset(FunctionID funcId) {
        ILNativeMap maps[64];
        ULONG32 count = 0;
        if (FAILED(GetILToNativeMapping(funcId, 64, &count, maps)) || count == 0) return 0;
        for (ULONG32 i = 0; i < count && i < 64; i++) {
            if (maps[i].ilOffset == 0)
                return maps[i].nativeStartOffset;
        }
        for (ULONG32 i = 0; i < count && i < 64; i++) {
            if ((int)maps[i].ilOffset >= 0)
                return maps[i].nativeStartOffset;
        }
        return 0;
    }
};

// Forward declarations for assembly stubs and callbacks.
extern "C" void FunctionEnterNaked();
extern "C" void FunctionLeaveNaked();
extern "C" void FunctionTailcallNaked();
extern "C" UINT_PTR __stdcall MixDbgFunctionIDMapper(FunctionID funcId, BOOL* pbHookFunction);

class MixDbgProfiler; // forward
static MixDbgProfiler* g_pProfiler = nullptr;

// ============================================================================
// MixDbgProfiler — ICorProfilerCallback2 implementation
//
// Derives from IUnknown only (we don't have the ICorProfilerCallback2 header).
// Virtual methods are declared in the exact vtable order specified by corprof.idl.
// MSVC single-inheritance guarantees methods appear in declaration order in the
// vtable, so the layout matches what the CLR expects.
//
// Only Initialize, Shutdown, and JITCompilationFinished do real work.
// All other callbacks return S_OK immediately.
// ============================================================================
class MixDbgProfiler : public IUnknown {
    volatile LONG m_refCount;
    ProfilerInfo* m_pInfo;
    HANDLE m_hPipe;
    HANDLE m_hAckEvent;  // Signaled by MixDbg after processing a notification.
    HANDLE m_hRehookEvent; // Signaled by MixDbg on Continue — re-enables enter/leave hooks.
    CRITICAL_SECTION m_pipeLock;

    // Exact (assembly, token) pairs that have breakpoints — only block JIT for these.
    // All other methods send notifications without blocking.
    static const int MAX_WATCH = 32;
    struct WatchEntry { char assembly[256]; unsigned int token; };
    WatchEntry m_watchEntries[MAX_WATCH];
    int m_watchCount;

    void WriteToPipe(const char* data, int len) {
        EnterCriticalSection(&m_pipeLock);
        if (m_hPipe != INVALID_HANDLE_VALUE) {
            DWORD written;
            WriteFile(m_hPipe, data, (DWORD)len, &written, nullptr);
        }
        LeaveCriticalSection(&m_pipeLock);
    }

public: // Accessed by static FunctionIDMapper callback — no vtable impact (non-virtual).
    // Check if an (assembly, token) pair is in the watch list.
    bool IsWatchedMethod(const char* asmName, unsigned int token) {
        for (int i = 0; i < m_watchCount; i++) {
            if (m_watchEntries[i].token == token &&
                _stricmp(m_watchEntries[i].assembly, asmName) == 0)
                return true;
        }
        return false;
    }

    // --- Enter/leave hook data (all non-virtual, no vtable impact) ---

    // Cached info for FunctionIDs that have hooks enabled.
    struct FunctionWatchInfo { mdToken token; UINT_PTR codeStart; char assembly[256]; };
    static const int MAX_WATCHED_FUNCS = 64;
    struct FuncSlot { FunctionID id; FunctionWatchInfo info; bool used; };
    FuncSlot m_funcSlots[MAX_WATCHED_FUNCS] = {};
    CRITICAL_SECTION m_funcLock;

public: // Accessed by static FunctionIDMapper — no vtable impact (all non-virtual).
    bool m_hooksActive = false;

    static void ExtractAssemblyName(const WCHAR* modulePath, char* out, int outSize) {
        const WCHAR* fileName = modulePath;
        for (const WCHAR* p = modulePath; *p; p++)
            if (*p == L'\\' || *p == L'/') fileName = p + 1;
        WCHAR asmW[256] = {};
        wcsncpy_s(asmW, 256, fileName, _TRUNCATE);
        WCHAR* dot = wcsrchr(asmW, L'.');
        if (dot) *dot = L'\0';
        WideCharToMultiByte(CP_UTF8, 0, asmW, -1, out, outSize, nullptr, nullptr);
    }

public:
    // Public non-virtual accessors for use by static callbacks (no vtable impact).
    ProfilerInfo* GetInfo() { return m_pInfo; }

    void RegisterWatchedFunction(FunctionID funcId, const FunctionWatchInfo& info) {
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

    const FunctionWatchInfo* FindWatchedFunction(FunctionID funcId) {
        for (int i = 0; i < MAX_WATCHED_FUNCS; i++) {
            if (m_funcSlots[i].used && m_funcSlots[i].id == funcId)
                return &m_funcSlots[i].info;
        }
        return nullptr;
    }

    void OnFunctionEnter(FunctionID funcId) {
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

public:
    MixDbgProfiler() : m_refCount(1), m_pInfo(nullptr),
        m_hPipe(INVALID_HANDLE_VALUE), m_hAckEvent(nullptr), m_hRehookEvent(nullptr), m_watchCount(0) {
        memset(m_watchEntries, 0, sizeof(m_watchEntries));
        InitializeCriticalSection(&m_pipeLock);
        InitializeCriticalSection(&m_funcLock);
    }

    ~MixDbgProfiler() {
        delete m_pInfo;
        if (m_hPipe != INVALID_HANDLE_VALUE)
            CloseHandle(m_hPipe);
        if (m_hAckEvent) CloseHandle(m_hAckEvent);
        if (m_hRehookEvent) CloseHandle(m_hRehookEvent);
        DeleteCriticalSection(&m_pipeLock);
        DeleteCriticalSection(&m_funcLock);
    }

    // ========================================================================
    // IUnknown
    // ========================================================================
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
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

    ULONG STDMETHODCALLTYPE AddRef() override {
        return InterlockedIncrement(&m_refCount);
    }

    ULONG STDMETHODCALLTYPE Release() override {
        LONG ref = InterlockedDecrement(&m_refCount);
        if (ref == 0) delete this;
        return (ULONG)ref;
    }

    // ========================================================================
    // ICorProfilerCallback — 67 methods in vtable order (slots 3-69)
    // ========================================================================

    // Slot 3: Initialize — called by the CLR when the profiler loads.
    virtual HRESULT STDMETHODCALLTYPE Initialize(IUnknown* pICorProfilerInfoUnk) {
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

        if (m_watchCount > 0) {
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

    // Slot 4: Shutdown
    virtual HRESULT STDMETHODCALLTYPE Shutdown() {
        EnterCriticalSection(&m_pipeLock);
        if (m_hPipe != INVALID_HANDLE_VALUE) {
            CloseHandle(m_hPipe);
            m_hPipe = INVALID_HANDLE_VALUE;
        }
        LeaveCriticalSection(&m_pipeLock);
        return S_OK;
    }

    // Slot 5-8: AppDomain callbacks
    virtual HRESULT STDMETHODCALLTYPE AppDomainCreationStarted(AppDomainID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE AppDomainCreationFinished(AppDomainID, HRESULT) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE AppDomainShutdownStarted(AppDomainID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE AppDomainShutdownFinished(AppDomainID, HRESULT) { return S_OK; }

    // Slot 9-12: Assembly callbacks
    virtual HRESULT STDMETHODCALLTYPE AssemblyLoadStarted(AssemblyID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE AssemblyLoadFinished(AssemblyID, HRESULT) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE AssemblyUnloadStarted(AssemblyID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE AssemblyUnloadFinished(AssemblyID, HRESULT) { return S_OK; }

    // Slot 13-17: Module callbacks
    virtual HRESULT STDMETHODCALLTYPE ModuleLoadStarted(ModuleID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ModuleLoadFinished(ModuleID, HRESULT) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ModuleUnloadStarted(ModuleID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ModuleUnloadFinished(ModuleID, HRESULT) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ModuleAttachedToAssembly(ModuleID, AssemblyID) { return S_OK; }

    // Slot 18-21: Class callbacks
    virtual HRESULT STDMETHODCALLTYPE ClassLoadStarted(ClassID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ClassLoadFinished(ClassID, HRESULT) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ClassUnloadStarted(ClassID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ClassUnloadFinished(ClassID, HRESULT) { return S_OK; }

    // Slot 22: FunctionUnloadStarted
    virtual HRESULT STDMETHODCALLTYPE FunctionUnloadStarted(FunctionID) { return S_OK; }

    // Slot 23: JITCompilationStarted
    virtual HRESULT STDMETHODCALLTYPE JITCompilationStarted(FunctionID, BOOL) { return S_OK; }

    // Slot 24: JITCompilationFinished — the main callback we care about.
    // Called by the CLR after a method is JIT-compiled. We extract the metadata
    // token, native code address, and assembly name, then send them to MixDbg.
    virtual HRESULT STDMETHODCALLTYPE JITCompilationFinished(
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
        const WCHAR* fileName = modulePath;
        for (const WCHAR* p = modulePath; *p; p++) {
            if (*p == L'\\' || *p == L'/')
                fileName = p + 1;
        }
        WCHAR assemblyName[256] = {};
        wcsncpy_s(assemblyName, 256, fileName, _TRUNCATE);
        WCHAR* dot = wcsrchr(assemblyName, L'.');
        if (dot) *dot = L'\0';

        // Format notification line and send to MixDbg.
        char asmUtf8[256] = {};
        WideCharToMultiByte(CP_UTF8, 0, assemblyName, -1, asmUtf8, 256, nullptr, nullptr);

        char line[4096]; // Large enough for IL-to-native mapping entries.
        int lineLen;

        if (m_hooksActive) {
            // With enter/leave hooks: send prefixed JIT notification.
            // For watched methods, include IL-to-native mapping so MixDbg can set
            // hardware BPs at exact source lines (not just method entry).
            lineLen = sprintf_s(line, sizeof(line), "JIT:%08X:%016llX:%08X:%s",
                (unsigned int)token, (unsigned long long)(UINT_PTR)codeStart,
                (unsigned int)codeSize, asmUtf8);

            if (lineLen > 0 && IsWatchedMethod(asmUtf8, token)) {
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

    // Slot 25-28: JIT cache and inlining
    virtual HRESULT STDMETHODCALLTYPE JITCachedFunctionSearchStarted(FunctionID, BOOL*) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE JITCachedFunctionSearchFinished(FunctionID, int) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE JITFunctionPitched(FunctionID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE JITInlining(FunctionID, FunctionID, BOOL*) { return S_OK; }

    // Slot 29-31: Thread callbacks
    virtual HRESULT STDMETHODCALLTYPE ThreadCreated(ThreadID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ThreadDestroyed(ThreadID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ThreadAssignedToOSThread(ThreadID, DWORD) { return S_OK; }

    // Slot 32-39: Remoting callbacks (deprecated in .NET Core, still in vtable)
    virtual HRESULT STDMETHODCALLTYPE RemotingClientInvocationStarted() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RemotingClientSendingMessage(GUID*, BOOL) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RemotingClientReceivingReply(GUID*, BOOL) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RemotingClientInvocationFinished() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RemotingServerReceivingMessage(GUID*, BOOL) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RemotingServerInvocationStarted() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RemotingServerInvocationReturned() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RemotingServerSendingReply(GUID*, BOOL) { return S_OK; }

    // Slot 40-41: Transition callbacks
    virtual HRESULT STDMETHODCALLTYPE UnmanagedToManagedTransition(FunctionID, int) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ManagedToUnmanagedTransition(FunctionID, int) { return S_OK; }

    // Slot 42-46: Runtime suspend/resume
    virtual HRESULT STDMETHODCALLTYPE RuntimeSuspendStarted(int) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RuntimeSuspendFinished() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RuntimeSuspendAborted() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RuntimeResumeStarted() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RuntimeResumeFinished() { return S_OK; }

    // Slot 47-51: GC/object callbacks
    virtual HRESULT STDMETHODCALLTYPE MovedReferences(ULONG, ObjectID*, ObjectID*, ULONG*) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ObjectAllocated(ObjectID, ClassID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ObjectsAllocatedByClass(ULONG, ClassID*, ULONG*) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ObjectReferences(ObjectID, ClassID, ULONG, ObjectID*) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RootReferences(ULONG, ObjectID*) { return S_OK; }

    // Slot 52: ExceptionThrown
    virtual HRESULT STDMETHODCALLTYPE ExceptionThrown(ObjectID) { return S_OK; }

    // Slot 53-59: Exception search/handler
    virtual HRESULT STDMETHODCALLTYPE ExceptionSearchFunctionEnter(FunctionID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ExceptionSearchFunctionLeave() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ExceptionSearchFilterEnter(FunctionID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ExceptionSearchFilterLeave() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ExceptionSearchCatcherFound(FunctionID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ExceptionOSHandlerEnter(UINT_PTR) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ExceptionOSHandlerLeave(UINT_PTR) { return S_OK; }

    // Slot 60-65: Exception unwind/catcher
    virtual HRESULT STDMETHODCALLTYPE ExceptionUnwindFunctionEnter(FunctionID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ExceptionUnwindFunctionLeave() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ExceptionUnwindFinallyEnter(FunctionID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ExceptionUnwindFinallyLeave() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ExceptionCatcherEnter(FunctionID, ObjectID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ExceptionCatcherLeave() { return S_OK; }

    // Slot 66-69: COM classic vtable and CLR catcher
    virtual HRESULT STDMETHODCALLTYPE COMClassicVTableCreated(ClassID, REFGUID, void*, ULONG) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE COMClassicVTableDestroyed(ClassID, REFGUID, void*) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ExceptionCLRCatcherFound() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE ExceptionCLRCatcherExecute() { return S_OK; }

    // ========================================================================
    // ICorProfilerCallback2 — 8 methods (slots 70-77)
    // ========================================================================
    virtual HRESULT STDMETHODCALLTYPE ThreadNameChanged(ThreadID, ULONG, WCHAR*) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE GarbageCollectionStarted(int, BOOL*, int) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE SurvivingReferences(ULONG, ObjectID*, ULONG*) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE GarbageCollectionFinished() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE FinalizeableObjectQueued(DWORD, ObjectID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RootReferences2(ULONG, ObjectID*, int*, int*, UINT_PTR*) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE HandleCreated(GCHandleID, ObjectID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE HandleDestroyed(GCHandleID) { return S_OK; }
};

// ============================================================================
// FunctionIDMapper — called by CLR for every function to decide if hooks fire
// ============================================================================
extern "C" UINT_PTR __stdcall MixDbgFunctionIDMapper(FunctionID funcId, BOOL* pbHookFunction) {
    *pbHookFunction = FALSE;
    if (!g_pProfiler || !g_pProfiler->GetInfo()) return funcId;

    ClassID classId = 0; ModuleID moduleId = 0; mdToken token = 0;
    if (FAILED(g_pProfiler->GetInfo()->GetFunctionInfo(funcId, &classId, &moduleId, &token)))
        return funcId;

    WCHAR modulePath[512] = {}; ULONG pathLen = 0; AssemblyID asmId = 0; LPCBYTE baseAddr = nullptr;
    if (FAILED(g_pProfiler->GetInfo()->GetModuleInfo(moduleId, &baseAddr, 512, &pathLen, modulePath, &asmId)))
        return funcId;
    if (modulePath[0] == L'\0') return funcId;

    char asmUtf8[256] = {};
    MixDbgProfiler::ExtractAssemblyName(modulePath, asmUtf8, sizeof(asmUtf8));

    if (g_pProfiler->IsWatchedMethod(asmUtf8, token)) {
        // Only enable hooks if the profiler has them active.
        if (g_pProfiler->m_hooksActive)
            *pbHookFunction = TRUE;
        LPCBYTE codeStart = nullptr; ULONG codeSize = 0;
        g_pProfiler->GetInfo()->GetCodeInfo(funcId, &codeStart, &codeSize);

        MixDbgProfiler::FunctionWatchInfo info;
        info.token = token;
        info.codeStart = (UINT_PTR)codeStart;
        strncpy_s(info.assembly, 256, asmUtf8, _TRUNCATE);
        g_pProfiler->RegisterWatchedFunction(funcId, info);
    }
    return funcId;
}

// Enter/Leave/Tailcall C++ impls (called from asm stubs in EnterLeaveStubs.asm)
extern "C" void FunctionEnterImpl(FunctionID funcId) {
    if (g_pProfiler) g_pProfiler->OnFunctionEnter(funcId);
}
extern "C" void FunctionLeaveImpl(FunctionID) {}
extern "C" void FunctionTailcallImpl(FunctionID) {}

// ============================================================================
// ClassFactory — creates MixDbgProfiler instances
// ============================================================================
class ClassFactory : public IClassFactory {
    volatile LONG m_refCount;

public:
    ClassFactory() : m_refCount(1) {}

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&m_refCount); }
    ULONG STDMETHODCALLTYPE Release() override {
        LONG ref = InterlockedDecrement(&m_refCount);
        if (ref == 0) delete this;
        return (ULONG)ref;
    }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override {
        if (pUnkOuter) return CLASS_E_NOAGGREGATION;
        auto* profiler = new MixDbgProfiler();
        HRESULT hr = profiler->QueryInterface(riid, ppv);
        profiler->Release();
        return hr;
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }
};

// ============================================================================
// DLL exports — called by the CLR to instantiate the profiler.
// DllGetClassObject and DllCanUnloadNow are already declared in combaseapi.h;
// we just provide the implementations here. The .def file marks them as exports.
// ============================================================================

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv) {
    if (rclsid != CLSID_MixDbgProfiler)
        return CLASS_E_CLASSNOTAVAILABLE;

    auto* factory = new ClassFactory();
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow() {
    return S_FALSE; // Never unload — profiler lifetime matches process lifetime.
}
