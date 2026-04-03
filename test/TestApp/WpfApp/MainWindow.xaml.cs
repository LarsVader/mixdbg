using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using CliWrapper;

namespace WpfApp
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            if (App.AutoTest)
                Loaded += OnAutoTest;
        }

        private void OnAutoTest(object sender, RoutedEventArgs e)
        {
            // Sequence: Add (JITs) → Add (bp hits) → [gap] → Multiply (JITs) → Multiply (bp hits) → exit
            // No PrepareMethod — matches the real manual debugging flow.
            // --auto-test-slow: 15s initial delay simulates a slow user, stressing the
            // CreateRuntime budget (7+ runtimes burned polling before first JIT).
            int firstDelay = App.AutoTestSlow ? 15 : 3;
            ScheduleActions(
                (firstDelay, () => OnAddClick(this, new RoutedEventArgs())),  // JITs OnAddClick
                (4, () => OnAddClick(this, new RoutedEventArgs())),           // bp hits
                (8, () => OnMultiplyClick(this, new RoutedEventArgs())),      // JITs OnMultiplyClick (after gap)
                (4, () => OnMultiplyClick(this, new RoutedEventArgs())),      // bp hits
                (3, () => Close()));
        }

        private static void ScheduleActions(params (int DelaySec, Action Action)[] steps)
        {
            if (steps.Length == 0) return;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(steps[0].DelaySec) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                steps[0].Action();
                ScheduleActions(steps[1..]);
            };
            timer.Start();
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            if (TryGetInputs(out int a, out int b))
            {
                int result = ManagedCalculator.Add(a, b);
                ResultText.Text = $"{a} + {b} = {result}";
            }
        }

        private void OnMultiplyClick(object sender, RoutedEventArgs e)
        {
            if (TryGetInputs(out int a, out int b))
            {
                int result = ManagedCalculator.Multiply(a, b);
                ResultText.Text = $"{a} × {b} = {result}";
            }
        }

        private bool TryGetInputs(out int a, out int b)
        {
            a = 0;
            b = 0;
            if (int.TryParse(TextBoxA.Text, out a) && int.TryParse(TextBoxB.Text, out b))
                return true;

            ResultText.Text = "Please enter valid integers.";
            return false;
        }
    }
}
