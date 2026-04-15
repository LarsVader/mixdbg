using System.Linq;
using System.Windows;

namespace WpfApp;

public partial class App : Application
{
    internal static bool AutoTest { get; private set; }
    internal static bool AutoTestSlow { get; private set; }
    internal static bool AutoTestDouble { get; private set; }
    internal static bool AutoTestComplex { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        AutoTest = e.Args.Contains("--auto-test") || e.Args.Contains("--auto-test-slow") || e.Args.Contains("--auto-test-double") || e.Args.Contains("--auto-test-complex");
        AutoTestSlow = e.Args.Contains("--auto-test-slow");
        AutoTestDouble = e.Args.Contains("--auto-test-double");
        AutoTestComplex = e.Args.Contains("--auto-test-complex");
        base.OnStartup(e);
    }
}
