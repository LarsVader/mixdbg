// FunctionCallbacks.cpp — Static function callbacks for CLR profiling hooks.
//
// These are free-standing extern "C" functions called by the CLR or by the
// assembly stubs in EnterLeaveStubs.asm. They delegate to the global
// MixDbgProfiler instance.

#include "MixDbgProfiler.h"

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

    // Only enable ENTER/LEAVE hooks for methods with exact WATCH tokens.
    if (g_pProfiler->IsWatchedMethod(asmUtf8, token) && g_pProfiler->m_hooksActive) {
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

// ============================================================================
// Enter/Leave/Tailcall C++ impls (called from asm stubs in EnterLeaveStubs.asm)
// ============================================================================

extern "C" void FunctionEnterImpl(FunctionID funcId) {
    if (g_pProfiler) g_pProfiler->OnFunctionEnter(funcId);
}

extern "C" void FunctionLeaveImpl(FunctionID funcId) {
    if (g_pProfiler) g_pProfiler->OnFunctionLeave(funcId);
}

extern "C" void FunctionTailcallImpl(FunctionID funcId) {
    // Tailcalls are also "leaving" this activation — treat identically to LEAVE.
    if (g_pProfiler) g_pProfiler->OnFunctionLeave(funcId);
}
