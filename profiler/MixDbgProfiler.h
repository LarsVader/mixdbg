// MixDbgProfiler.h — ICorProfilerCallback2 implementation.
//
// Derives from IUnknown only (we don't have the ICorProfilerCallback2 header).
// Virtual methods are declared in the exact vtable order specified by corprof.idl.
// MSVC single-inheritance guarantees methods appear in declaration order in the
// vtable, so the layout matches what the CLR expects.
//
// Only Initialize, Shutdown, and JITCompilationFinished do real work.
// All other callbacks return S_OK immediately.

#pragma once

#include "CoreClrTypes.h"
#include "ProfilerInfo.h"

// Forward declarations for assembly stubs and callbacks.
extern "C" void FunctionEnterNaked();
extern "C" void FunctionLeaveNaked();
extern "C" void FunctionTailcallNaked();
extern "C" UINT_PTR __stdcall MixDbgFunctionIDMapper(FunctionID funcId, BOOL* pbHookFunction);

class MixDbgProfiler : public IUnknown {
    volatile LONG m_refCount;
    ProfilerInfo* m_pInfo;
    HANDLE m_hPipe;
    HANDLE m_hCmdPipe;   // Command pipe — reads WATCH commands from MixDbg at runtime.
    HANDLE m_hAckEvent;  // Signaled by MixDbg after processing the first ENTER (count 0→1).
    CRITICAL_SECTION m_pipeLock;
    CRITICAL_SECTION m_watchLock; // Protects m_watchEntries/m_watchCount (written by cmd reader thread).

    // Exact (assembly, token) pairs that have breakpoints — only block JIT for these.
    // All other methods send notifications without blocking.
    // Updated at runtime via WATCH commands from MixDbg (mid-session breakpoints).
    static const int MAX_WATCH = 64;
    struct WatchEntry { char assembly[256]; unsigned int token; };
    WatchEntry m_watchEntries[MAX_WATCH];
    volatile int m_watchCount;

    void CmdReaderLoop();

    void WriteToPipe(const char* data, int len);

public: // Accessed by static FunctionIDMapper callback — no vtable impact (non-virtual).
    // Check if an (assembly, token) pair is in the exact watch list.
    bool IsWatchedMethod(const char* asmName, unsigned int token);

    // --- Enter/leave hook data (all non-virtual, no vtable impact) ---

    // Cached info for FunctionIDs that have hooks enabled.
    struct FunctionWatchInfo { mdToken token; UINT_PTR codeStart; char assembly[256]; };
    static const int MAX_WATCHED_FUNCS = 64;
    struct FuncSlot { FunctionID id; FunctionWatchInfo info; bool used; };
    FuncSlot m_funcSlots[MAX_WATCHED_FUNCS] = {};
    CRITICAL_SECTION m_funcLock;

public: // Accessed by static FunctionIDMapper — no vtable impact (all non-virtual).
    bool m_hooksActive = false;

    static void ExtractAssemblyName(const WCHAR* modulePath, char* out, int outSize);

public:
    // Public non-virtual accessors for use by static callbacks (no vtable impact).
    ProfilerInfo* GetInfo() { return m_pInfo; }

    void RegisterWatchedFunction(FunctionID funcId, const FunctionWatchInfo& info);
    const FunctionWatchInfo* FindWatchedFunction(FunctionID funcId);
    void OnFunctionEnter(FunctionID funcId);
    void OnFunctionLeave(FunctionID funcId);

public:
    MixDbgProfiler();
    ~MixDbgProfiler();

    // ========================================================================
    // IUnknown
    // ========================================================================
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override;
    ULONG STDMETHODCALLTYPE AddRef() override;
    ULONG STDMETHODCALLTYPE Release() override;

    // ========================================================================
    // ICorProfilerCallback — 67 methods in vtable order (slots 3-69)
    // ========================================================================

    // Slot 3: Initialize — called by the CLR when the profiler loads.
    virtual HRESULT STDMETHODCALLTYPE Initialize(IUnknown* pICorProfilerInfoUnk);

    // Slot 4: Shutdown
    virtual HRESULT STDMETHODCALLTYPE Shutdown();

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
    virtual HRESULT STDMETHODCALLTYPE JITCompilationFinished(
        FunctionID functionId, HRESULT hrStatus, BOOL fIsSafeToBlock);

    // Slot 25-28: JIT cache and inlining
    virtual HRESULT STDMETHODCALLTYPE JITCachedFunctionSearchStarted(FunctionID, BOOL*) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE JITCachedFunctionSearchFinished(FunctionID, int) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE JITFunctionPitched(FunctionID) { return S_OK; }

    // Slot 28: JITInlining — called before JIT inlines a callee into a caller.
    // Disables inlining for watched methods so every call fires FunctionEnter/Leave.
    virtual HRESULT STDMETHODCALLTYPE JITInlining(FunctionID callerId, FunctionID calleeId, BOOL* pfShouldInline);

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

// Global profiler instance — accessed by static callbacks (FunctionIDMapper, FunctionEnterImpl).
extern MixDbgProfiler* g_pProfiler;
