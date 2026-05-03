using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace WpfApp.ViewModels;

public class CalculatorViewModel : INotifyPropertyChanged
{
    public int InputA { get; set => SetField(ref field, value); } = 7;

    public int InputB { get; set => SetField(ref field, value); } = 3;

    public string Result { get; set => SetField(ref field, value); } = string.Empty;

    public string StatusMessage { get; set => SetField(ref field, value); } = "Ready";

    public ICommand AddCommand { get => field ??= new RelayCommand(_ =>
        {
            int sum = CliWrapper.ManagedCalculator.Add(InputA, InputB);
            Result = $"{InputA} + {InputB} = {sum}";
            StatusMessage = "Addition complete";
        }); } = null;

    /// <summary>test</summary>
    public ICommand MultiplyCommand { get => field ??= new RelayCommand(_ =>
        {
            int product = CliWrapper.ManagedCalculator.Multiply(InputA, InputB);
            Result = $"{InputA} x {InputB} = {product}";
            StatusMessage = "Multiplication complete";
        }); } = null;

    public ICommand FibonacciCommand { get => field ??= new RelayCommand(_ =>
        {
            int fib = CliWrapper.ManagedCalculator.Fibonacci(InputA);
            Result = $"Fibonacci({InputA}) = {fib}";
            StatusMessage = "Fibonacci complete";
        }); } = null;

    public event PropertyChangedEventHandler PropertyChanged;

    private void SetField<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
    {
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class RelayCommand(Action<object> execute, Func<object, bool> canExecute = null) : ICommand
{
    public bool CanExecute(object parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object parameter) => execute(parameter);

    public event EventHandler CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
