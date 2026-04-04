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
            // Hide window so integration tests run headlessly.
            Hide();

            // Sequence: Add → Multiply → exit
            // --auto-test-slow: 15s initial delay simulates a slow user.
            // --auto-test-double: clicks each button twice (first call JITs, second hits hw BP).
            int firstDelay = App.AutoTestSlow ? 15 : 3;
            if (App.AutoTestDouble)
            {
                // First call JITs the method. DAC needs ~12s to detect JIT'd code.
                // Second call (after 15s gap) hits the hardware breakpoint.
                ScheduleActions(
                    (firstDelay, () => OnAddClick(this, new RoutedEventArgs())),      // JIT + run (no bp yet)
                    (15, () => OnAddClick(this, new RoutedEventArgs())),              // hw bp fires
                    (15, () => OnMultiplyClick(this, new RoutedEventArgs())),         // JIT + run (no bp yet)
                    (15, () => OnMultiplyClick(this, new RoutedEventArgs())),         // hw bp fires
                    (5, () => Close()));
            }
            else
            {
                ScheduleActions(
                    (firstDelay, () => OnAddClick(this, new RoutedEventArgs())),
                    (8, () => OnMultiplyClick(this, new RoutedEventArgs())),
                    (3, () => Close()));
            }
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
