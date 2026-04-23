using System.Windows;
using WpfApp.ViewModels;

namespace WpfApp;

public partial class BindingTestWindow : Window
{
    public BindingTestWindow()
    {
        InitializeComponent();
        DataContext = new CalculatorViewModel();
    }
}
