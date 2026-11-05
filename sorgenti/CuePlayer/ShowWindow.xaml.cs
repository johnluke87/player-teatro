using System.Windows;
using System.Windows.Input;
using CuePlayer.ViewModels;

namespace CuePlayer;

/// <summary>Full-screen view for the performance. Same data as the main window, nothing editable.</summary>
public partial class ShowWindow : Window
{
    private readonly MainViewModel _vm;

    public ShowWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += (_, _) => Keyboard.Focus(this);
        Activated += (_, _) => MainWindow.EnsureKeyboardFocus(this);
        PreviewMouseUp += (_, _) => MainWindow.EnsureKeyboardFocus(this);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { Close(); e.Handled = true; return; }
        MainWindow.HandleShortcut(_vm, e);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Prev_Click(object sender, RoutedEventArgs e) => _vm.MoveNext(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => _vm.MoveNext(+1);
}
