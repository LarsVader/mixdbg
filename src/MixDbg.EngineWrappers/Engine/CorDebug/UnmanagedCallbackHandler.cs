using ClrDebug;

namespace MixDbg.Engine.Clr;

/// <summary>
/// Wraps <see cref="CorDebugUnmanagedCallback"/> from ClrDebug for receiving
/// native debug events in ICorDebug interop mode. Each callback runs on the
/// CLR's debug helper thread.
/// </summary>
internal sealed class UnmanagedCallbackHandler
{
    public CorDebugUnmanagedCallback Callback { get; } = new();

    /// <summary>
    /// Fired when a native debug event occurs (breakpoint, exception, etc.).
    /// Parameters: (dwDebugEventCode, exceptionAddress, outOfBand).
    /// Must call <c>process.Continue(outOfBand)</c> to resume.
    /// </summary>
    public event Action<int, ulong, bool>? DebugEvent;

    /// <summary>Optional logger for debugging interop mode issues.</summary>
    public Action<string>? Log { get; set; }

    public UnmanagedCallbackHandler()
    {
        Callback.OnDebugEvent += (s, e) =>
        {
            var evt = e.DebugEvent;
            int code = (int)evt.dwDebugEventCode;
            bool oob = e.OutOfBand;

            Log?.Invoke($"Unmanaged event: code={code} ({DebugEventName(code)}) oob={oob}");

            ulong exAddr = 0;
            if (code == 1) // EXCEPTION_DEBUG_EVENT
            {
                try
                {
                    var exField = evt.GetType().GetField("u");
                    if (exField != null)
                    {
                        var u = exField.GetValue(evt);
                        var exDbgInfo = u?.GetType().GetField("Exception")?.GetValue(u);
                        var exRecord = exDbgInfo?.GetType().GetField("ExceptionRecord")?.GetValue(exDbgInfo);
                        var addrField = exRecord?.GetType().GetField("ExceptionAddress");
                        if (addrField != null)
                            exAddr = (ulong)(IntPtr)addrField.GetValue(exRecord)!;
                    }
                }
                catch { }
            }

            DebugEvent?.Invoke(code, exAddr, oob);
        };
    }

    private static string DebugEventName(int code) => code switch
    {
        1 => "EXCEPTION",
        2 => "CREATE_THREAD",
        3 => "CREATE_PROCESS",
        4 => "EXIT_THREAD",
        5 => "EXIT_PROCESS",
        6 => "LOAD_DLL",
        7 => "UNLOAD_DLL",
        8 => "OUTPUT_DEBUG_STRING",
        9 => "RIP",
        _ => $"UNKNOWN({code})",
    };
}
