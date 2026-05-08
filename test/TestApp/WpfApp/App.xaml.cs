using System.Linq;
using System.Windows;

namespace WpfApp;

public partial class App : Application
{
    internal static bool AutoTest { get; private set; }
    internal static bool AutoTestSlow { get; private set; }
    internal static bool AutoTestDouble { get; private set; }
    internal static bool AutoTestComplex { get; private set; }
    internal static bool AutoTestLate { get; private set; }
    /// <summary>
    /// Attach test mode: idle for a long pre-roll so a separately-spawned
    /// MixDbg can attach via diagnostic IPC and set breakpoints before the
    /// first click runs.
    /// </summary>
    internal static bool AutoTestAttach { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        AutoTest = e.Args.Contains("--auto-test") || e.Args.Contains("--auto-test-slow") || e.Args.Contains("--auto-test-double") || e.Args.Contains("--auto-test-complex") || e.Args.Contains("--auto-test-late") || e.Args.Contains("--auto-test-attach");
        AutoTestSlow = e.Args.Contains("--auto-test-slow");
        AutoTestDouble = e.Args.Contains("--auto-test-double");
        AutoTestComplex = e.Args.Contains("--auto-test-complex");
        AutoTestLate = e.Args.Contains("--auto-test-late");
        AutoTestAttach = e.Args.Contains("--auto-test-attach");
        base.OnStartup(e);
    }
}
