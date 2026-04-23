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
        Hide();
        int firstDelay = App.AutoTestSlow ? 15 : 3;
        if (App.AutoTestComplex)
        {   // 15s initial delay: ICorDebug needs time to init + convert pending BPs.
            ScheduleActions(
                (15, OnFibonacciClickAction), (8, OnCountPrimesClickAction),
                (8, OnFactorialClickAction), (8, OnAsyncCalcClickAction),
                (10, OnComplexClickAction), (8, Close));
        }
        else if (App.AutoTestDouble)
        {
            ScheduleActions(
                (firstDelay, OnAddClickAction), (15, OnAddClickAction),
                (15, OnMultiplyClickAction), (15, OnMultiplyClickAction), (5, Close));
        }
        else
        {
            ScheduleActions(
                (firstDelay, OnAddClickAction), (8, OnMultiplyClickAction), (3, Close));
        }
    }

    private void OnAddClickAction() => OnAddClick(this, new RoutedEventArgs());
    private void OnMultiplyClickAction() => OnMultiplyClick(this, new RoutedEventArgs());
    private void OnFibonacciClickAction() => OnFibonacciClick(this, new RoutedEventArgs());
    private void OnCountPrimesClickAction() => OnCountPrimesClick(this, new RoutedEventArgs());
    private void OnFactorialClickAction() => OnFactorialClick(this, new RoutedEventArgs());
    private void OnAsyncCalcClickAction() => OnAsyncCalcClick(this, new RoutedEventArgs());
    private void OnComplexClickAction() => OnComplexClick(this, new RoutedEventArgs());

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
    // ── Basic operations (line numbers below are referenced by integration tests) ──

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

    // ── New complex scenarios (below original methods to preserve line numbers) ──

    private void OnFibonacciClick(object sender, RoutedEventArgs e)
    {
        if (TryGetA(out int n))
        {
            int result = ManagedCalculator.Fibonacci(n);
            ResultText.Text = $"Fibonacci({n}) = {result}";
        }
    }

    private void OnCountPrimesClick(object sender, RoutedEventArgs e)
    {
        if (TryGetA(out int limit))
        {
            int result = ManagedCalculator.CountPrimes(limit);
            ResultText.Text = $"Primes up to {limit}: {result}";
        }
    }

    private void OnFactorialClick(object sender, RoutedEventArgs e)
    {
        if (TryGetA(out int n))
        {
            try
            {
                int result = ManagedCalculator.FactorialOrThrow(n);
                ResultText.Text = $"{n}! = {result}";
            }
            catch (Exception ex)
            {
                ResultText.Text = $"Caught: {ex.Message}";
            }
        }
    }

    private async void OnAsyncCalcClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetInputs(out int a, out int b))
            return;

        ResultText.Text = "Calculating async...";

        int fibResult = await Task.Run(() =>
        {
            int fib = ManagedCalculator.Fibonacci(a);
            return fib;
        });

        await Task.Delay(50);

        int sum = ManagedCalculator.SumRange(1, b);
        int combined = fibResult + sum;
        ResultText.Text = $"Async: Fib({a})={fibResult}, Sum(1..{b})={sum}, Total={combined}";
    }

    private void OnComplexClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetInputs(out int a, out int b))
            return;

        int multiplier = b;
        Func<int, int> scale = x => x * multiplier;

        List<int> numbers = [.. Enumerable.Range(1, a)];
        int threshold = a / 2;
        List<int> filtered = [.. numbers.Where(n => n > threshold)];

        int total = 0;
        foreach (int n in filtered)
        {
            int scaled = scale(n);
            int added = ManagedCalculator.Add(scaled, n);
            total += added;
        }

        int nested = ManagedCalculator.Add(
            ManagedCalculator.Multiply(a, b),
            ManagedCalculator.Fibonacci(Math.Min(a, 8)));

        ResultText.Text = $"Complex: loop={total}, nested={nested}, items={filtered.Count}";
    }

    private bool TryGetA(out int a)
    {
        if (int.TryParse(TextBoxA.Text, out a))
            return true;

        a = 0;
        ResultText.Text = "Please enter a valid integer for A.";
        return false;
    }

    public System.Windows.Input.ICommand TestCommand
    {
        get; set;
    }
}
