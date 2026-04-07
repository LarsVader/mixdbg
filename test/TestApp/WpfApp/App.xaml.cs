using System.Linq;
using System.Windows;

namespace WpfApp;

public partial class App : Application
{
    internal static bool AutoTest { get; private set; }
    internal static bool AutoTestSlow { get; private set; }
    internal static bool AutoTestDouble { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        AutoTest = e.Args.Contains("--auto-test") || e.Args.Contains("--auto-test-slow") || e.Args.Contains("--auto-test-double");
        AutoTestSlow = e.Args.Contains("--auto-test-slow");
        AutoTestDouble = e.Args.Contains("--auto-test-double");
        base.OnStartup(e);
    }
}
