using System.Runtime.InteropServices;

using ClrDebug;

namespace MixDbg.Engine.CorDebug;

/// <summary>
/// Bootstraps ICorDebug via dbgshim.dll. Handles process launch (suspended)
/// and runtime startup registration to obtain a <see cref="ClrDebug.CorDebug"/>
/// instance before any user code runs.
/// </summary>
internal static class DbgShimBootstrap
{
    /// <summary>
    /// Resolves the path to dbgshim.dll from the .NET runtime installation.
    /// Searches the dotnet shared framework directories for a matching architecture.
    /// </summary>
    public static string ResolveDbgShimPath()
    {
        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(dotnetRoot))
        {
            dotnetRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet");
        }

        string sharedDir = Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App");
        if (Directory.Exists(sharedDir))
        {
            // Search version directories in reverse order (newest first).
            foreach (string? versionDir in Directory.GetDirectories(sharedDir).OrderByDescending(d => d))
            {
                string path = Path.Combine(versionDir, "dbgshim.dll");
                if (File.Exists(path))
                    return path;
            }
        }

        // Fallback: search in PATH.
        foreach (string dir in Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? [])
        {
            string path = Path.Combine(dir, "dbgshim.dll");
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException(
            "Could not find dbgshim.dll. Ensure the .NET SDK is installed.");
    }

    /// <summary>
    /// Loads dbgshim.dll and returns a <see cref="DbgShim"/> instance.
    /// </summary>
    public static DbgShim LoadDbgShim()
    {
        string path = ResolveDbgShimPath();
        nint handle = NativeLibrary.Load(path);
        return new DbgShim(handle);
    }

    /// <summary>
    /// Launches a process suspended, registers for CLR runtime startup, resumes,
    /// and waits for the CLR to initialize. Returns the <see cref="ClrDebug.CorDebug"/>
    /// instance and the process ID.
    /// </summary>
    public static (ClrDebug.CorDebug CorDebug, int ProcessId) LaunchAndAttach(
        DbgShim dbgShim, string program, string? cwd, string[]? args, int timeoutMs = 30000)
    {
        // Build command line.
        string cmdLine = program;
        if (args is { Length: > 0 })
            cmdLine += " " + string.Join(" ", args);

        // Create the process suspended.
        CreateProcessForLaunchResult launchResult = dbgShim.CreateProcessForLaunch(cmdLine, bSuspendProcess: true, lpCurrentDirectory: cwd);
        int pid = launchResult.ProcessId;

        ClrDebug.CorDebug? corDebug = null;
        ManualResetEventSlim readyEvent = new(false);
        Exception? startupError = null;

        // Register for runtime startup notification.
        // The extension method overload accepts RuntimeStartupCallback which auto-wraps ICorDebug.
        nint cookie = dbgShim.RegisterForRuntimeStartup(
            pid,
            (pCorDebug, parameter, hr) =>
            {
                try
                {
                    if (hr != HRESULT.S_OK)
                    {
                        throw new InvalidOperationException(
                            $"Runtime startup failed: hr=0x{(int)hr:X8}");
                    }

                    corDebug = pCorDebug;
                }
                catch (Exception ex)
                {
                    startupError = ex;
                }
                finally
                {
                    readyEvent.Set();
                }
            });

        try
        {
            // Resume the process so the CLR can initialize.
            dbgShim.ResumeProcess(launchResult.ResumeHandle);

            // Wait for the CLR startup callback.
            if (!readyEvent.Wait(timeoutMs))
            {
                try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch { }
                throw new TimeoutException($"CLR did not initialize within {timeoutMs}ms");
            }

            return startupError != null
                ? throw startupError
                : corDebug == null
                ? throw new InvalidOperationException("Runtime startup callback did not provide ICorDebug")
                : ((ClrDebug.CorDebug CorDebug, int ProcessId))(corDebug, pid);
        }
        finally
        {
            dbgShim.UnregisterForRuntimeStartup(cookie);
            dbgShim.CloseResumeHandle(launchResult.ResumeHandle);
        }
    }
}