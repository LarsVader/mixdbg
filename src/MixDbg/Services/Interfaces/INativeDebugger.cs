using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services;

public interface INativeDebugger
{
    NativeDebuggerModel CreateModel();
    void Launch(NativeDebuggerModel model, string program, string? cwd, string? symbolPath);
    void Attach(NativeDebuggerModel model, uint pid, string? symbolPath);
    void Continue(NativeDebuggerModel model);
    void Break(NativeDebuggerModel model);
    void StepOver(NativeDebuggerModel model);
    void StepInto(NativeDebuggerModel model);
    void StepOut(NativeDebuggerModel model);
    Breakpoint[] SetBreakpoints(NativeDebuggerModel model, string filePath, SourceBreakpoint[] requested);
    StackFrame[] GetStackTrace(NativeDebuggerModel model, int maxFrames);
    DapThread[] GetThreads(NativeDebuggerModel model);
    int GetStoppedThreadId(NativeDebuggerModel model);
    void Terminate(NativeDebuggerModel model);
    void Detach(NativeDebuggerModel model);
}
