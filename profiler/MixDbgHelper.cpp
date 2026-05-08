// MixDbgHelper.cpp — exports called from rewritten managed IL.
//
// In attach mode, COR_PRF_MONITOR_ENTERLEAVE is rejected by the runtime, so
// the FunctionEnter/FunctionLeave hooks used in launch mode are unavailable.
// To synthesize ENTER/LEAVE notifications we ReJIT each watched method and
// inject calls to these exported functions at the method entry and inside
// an outer try/finally that wraps the original body.
//
// The IL rewriter (GetReJITParameters) emits two pinvoke methoddefs in the
// target module's metadata pointing at these exports — see the TODO in
// MixDbgProfiler.cpp::GetReJITParameters.
//
// Both functions are no-ops when no profiler is loaded so a stale rejitted
// method body cannot crash on detach.

#include "MixDbgProfiler.h"

extern "C" {

// Wide-char overloads to match the P/Invoke signature emitted by the IL
// rewriter: `[DllImport("MixDbgProfiler.dll", CharSet=Unicode,
// CallingConvention=StdCall)] static extern void MixDbgHelper_Enter(int token,
// string assembly)`.

__declspec(dllexport) void __stdcall MixDbgHelper_Enter(unsigned int token, const WCHAR* assembly) {
    if (!g_pProfiler) return;

    // Convert the UTF-16 assembly name to UTF-8 for the wire format.
    char asmUtf8[256] = {};
    if (assembly)
        WideCharToMultiByte(CP_UTF8, 0, assembly, -1, asmUtf8, sizeof(asmUtf8), nullptr, nullptr);

    DWORD tid = GetCurrentThreadId();
    char line[512];
    int len = sprintf_s(line, sizeof(line), "ENTER:%08X:0:%08X:%s\n",
        token, tid, asmUtf8);
    if (len <= 0) return;

    // Same blocking semantics as runtime-hook OnFunctionEnter — block on ACK
    // so MixDbg can install hardware BPs before the body runs.
    g_pProfiler->WriteToPipeFromHelper(line, len);
    HANDLE hAck = g_pProfiler->GetAckEventForHelper();
    if (hAck) WaitForSingleObject(hAck, 500);
}

__declspec(dllexport) void __stdcall MixDbgHelper_Leave(unsigned int token, const WCHAR* assembly) {
    if (!g_pProfiler) return;

    char asmUtf8[256] = {};
    if (assembly)
        WideCharToMultiByte(CP_UTF8, 0, assembly, -1, asmUtf8, sizeof(asmUtf8), nullptr, nullptr);

    DWORD tid = GetCurrentThreadId();
    char line[512];
    int len = sprintf_s(line, sizeof(line), "LEAVE:%08X:%08X:%s\n",
        token, tid, asmUtf8);
    if (len <= 0) return;

    g_pProfiler->WriteToPipeFromHelper(line, len);
    // Fire-and-forget — Leave never blocks.
}

} // extern "C"
