using System;
using System.Windows;
using System.Windows.Threading;
using CliWrapper;

namespace WpfApp;

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
                (firstDelay, OnAddClickAction),
                (15, OnAddClickAction),
                (15, OnMultiplyClickAction),
                (15, OnMultiplyClickAction),
                (5, Close));
        }
        else
        {
            ScheduleActions(
                (firstDelay, OnAddClickAction),
                (8, OnMultiplyClickAction),
                (3, Close));
        }
    }

    private void OnAddClickAction() => OnAddClick(this, new RoutedEventArgs());

    private void OnMultiplyClickAction() => OnMultiplyClick(this, new RoutedEventArgs());

    private static void ScheduleActions(params (int DelaySec, Action Action)[] steps)
    {
        if (steps.Length == 0) return;
        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(steps[0].DelaySec) };
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
        if (int.TryParse(TextBoxA.Text, out a) && int.TryParse(TextBoxB.Text, out b))
            return true;

        a = 0;
        b = 0;
        ResultText.Text = "Please enter valid integers.";
        return false;
    }
}
