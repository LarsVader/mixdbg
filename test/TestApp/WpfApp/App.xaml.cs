using System.Linq;
using System.Windows;

namespace WpfApp
{
    public partial class App : Application
    {
        internal static bool AutoTest { get; private set; }
        internal static bool AutoTestSlow { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            AutoTest = e.Args.Contains("--auto-test") || e.Args.Contains("--auto-test-slow");
            AutoTestSlow = e.Args.Contains("--auto-test-slow");
            base.OnStartup(e);
        }
    }
}
