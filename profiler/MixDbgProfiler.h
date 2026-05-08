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

    // True when loaded via InitializeForAttach. ENTER/LEAVE notifications come
    // from IL-rewritten P/Invoke calls (via MixDbgHelper exports) rather than
    // runtime FunctionEnter/Leave hooks; WATCH commands trigger RequestReJIT
    // instead of just registering the token.
    bool m_isAttachMode = false;

    static void ExtractAssemblyName(const WCHAR* modulePath, char* out, int outSize);

public:
    // Public non-virtual accessors for use by static callbacks (no vtable impact).
    ProfilerInfo* GetInfo() { return m_pInfo; }

    void RegisterWatchedFunction(FunctionID funcId, const FunctionWatchInfo& info);
    const FunctionWatchInfo* FindWatchedFunction(FunctionID funcId);
    void OnFunctionEnter(FunctionID funcId);
    void OnFunctionLeave(FunctionID funcId);

    // Used by MixDbgHelper exports (called from rewritten IL in attach mode).
    // Wraps WriteToPipe + ACK event access without exposing the underlying
    // pipe handles or critical section.
    void WriteToPipeFromHelper(const char* data, int len) { WriteToPipe(data, len); }
    HANDLE GetAckEventForHelper() const { return m_hAckEvent; }

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

    // Slot 42-48: Runtime suspend/resume + thread suspend/resume.
    // RuntimeThreadSuspended/RuntimeThreadResumed are part of corprof.idl's
    // ICorProfilerCallback declaration — omitting them shifts every later
    // slot by 2 and causes the CLR to call the wrong method when it expects
    // (e.g.) InitializeForAttach.
    virtual HRESULT STDMETHODCALLTYPE RuntimeSuspendStarted(int) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RuntimeSuspendFinished() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RuntimeSuspendAborted() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RuntimeResumeStarted() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RuntimeResumeFinished() { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RuntimeThreadSuspended(ThreadID) { return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE RuntimeThreadResumed(ThreadID) { return S_OK; }

    // Slot 49-53: GC/object callbacks
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

    // ========================================================================
    // ICorProfilerCallback3 — 3 methods (slots 78-80)
    // Required for profiler attach via the diagnostic IPC pipe.
    // ========================================================================

    // Slot 78: InitializeForAttach — called by the CLR when the profiler is
    // loaded via AttachProfiler (instead of the launch-time CORECLR_PROFILER
    // env vars). pvClientData is the blob passed to AttachProfiler — for
    // mixdbg it contains the pipe names + watch tokens (see
    // ProfilerClientDataBuilder on the C# side).
    virtual HRESULT STDMETHODCALLTYPE InitializeForAttach(
        IUnknown* pCorProfilerInfoUnk, void* pvClientData, UINT cbClientData);

    // Slot 79: ProfilerAttachComplete — fired after InitializeForAttach
    // returns. The runtime will not call any callbacks until then.
    virtual HRESULT STDMETHODCALLTYPE ProfilerAttachComplete() { return S_OK; }

    // Slot 80: ProfilerDetachSucceeded — fires after RequestProfilerDetach
    // unloads us. We have no state to flush here; Shutdown does the cleanup.
    virtual HRESULT STDMETHODCALLTYPE ProfilerDetachSucceeded() { return S_OK; }

    // ========================================================================
    // ICorProfilerCallback4 — 6 methods (slots 81-86)
    // Required for ReJIT — used in attach mode to rewrite already-JIT'd
    // methods so their entry/exit can be observed without the
    // FunctionEnter/Leave hooks (which are unavailable to attached profilers).
    // ========================================================================

    // Slot 81: ReJITCompilationStarted
    virtual HRESULT STDMETHODCALLTYPE ReJITCompilationStarted(
        FunctionID, ReJITID, BOOL) { return S_OK; }

    // Slot 82: GetReJITParameters — called once per ReJIT request. The profiler
    // hands the new IL (and any flags) to pFunctionControl->SetILFunctionBody.
    // For now this is a stub that retains the original IL — full IL rewriting
    // is the Phase B follow-up that injects MixDbgHelper.Enter/Leave calls
    // around the original body.
    virtual HRESULT STDMETHODCALLTYPE GetReJITParameters(
        ModuleID moduleId, mdMethodDef methodId, IUnknown* pFunctionControl);

    // Slot 83: ReJITCompilationFinished
    virtual HRESULT STDMETHODCALLTYPE ReJITCompilationFinished(
        FunctionID, ReJITID, HRESULT, BOOL) { return S_OK; }

    // Slot 84: ReJITError
    virtual HRESULT STDMETHODCALLTYPE ReJITError(
        ModuleID, mdMethodDef, FunctionID, HRESULT) { return S_OK; }

    // Slot 85: MovedReferences2 (GC, unused)
    virtual HRESULT STDMETHODCALLTYPE MovedReferences2(
        ULONG, ObjectID*, ObjectID*, SIZE_T*) { return S_OK; }

    // Slot 86: SurvivingReferences2 (GC, unused)
    virtual HRESULT STDMETHODCALLTYPE SurvivingReferences2(
        ULONG, ObjectID*, SIZE_T*) { return S_OK; }
};

// Global profiler instance — accessed by static callbacks (FunctionIDMapper, FunctionEnterImpl).
extern MixDbgProfiler* g_pProfiler;
