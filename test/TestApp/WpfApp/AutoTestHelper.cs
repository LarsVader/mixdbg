using System;
using System.Runtime.CompilerServices;
using System.Windows;

namespace WpfApp;

/// <summary>
/// Handles auto-test scheduling. Extracted from MainWindow to allow adding
/// new test modes without shifting line numbers in MainWindow.xaml.cs.
/// </summary>
internal static class AutoTestHelper
{
    internal static RoutedEventHandler CreateHandler(MainWindow window) =>
        (_, _) => RunAutoTest(window);

    private static void RunAutoTest(MainWindow window)
    {
        window.Hide();
        int firstDelay = App.AutoTestSlow ? 15 : 3;
        if (App.AutoTestComplex)
        {
            MainWindow.ScheduleActions(
                (5, window.OnFibonacciClickAction), (5, window.OnCountPrimesClickAction),
                (5, window.OnFactorialClickAction), (5, window.OnAsyncCalcClickAction),
                (5, window.OnComplexClickAction), (5, window.Close));
        }
        else if (App.AutoTestLate)
        {
            MainWindow.ScheduleActions(
                (firstDelay, window.OnAddClickAction),
                (10, () => OnLateSquareClick(window)),
                (5, window.Close));
        }
        else if (App.AutoTestDouble)
        {
            MainWindow.ScheduleActions(
                (firstDelay, window.OnAddClickAction), (15, window.OnAddClickAction),
                (15, window.OnMultiplyClickAction), (15, window.OnMultiplyClickAction),
                (5, window.Close));
        }
        else
        {
            MainWindow.ScheduleActions(
                (firstDelay, window.OnAddClickAction),
                (8, window.OnMultiplyClickAction), (3, window.Close));
        }
    }

    private static void OnLateSquareClick(MainWindow window)
    {
        int result = CallLateSquare(3);
        window.ResultText.Text = $"3² = {result}";
    }

    /// <summary>
    /// NoInlining ensures the JIT doesn't load LateCliWrapper.dll until this
    /// method is actually called — simulating a late-loaded C++/CLI assembly.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int CallLateSquare(int x) =>
        LateCliWrapper.LateCalculator.Square(x);
}
