using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CuePlayer.ViewModels;
using Microsoft.Win32;

namespace CuePlayer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(Dispatcher);
        DataContext = _vm;
        Loaded += (_, _) => _vm.Initialize(App.StartupFolder);
        SourceInitialized += (_, _) => UseDarkTitleBar(this);
        _vm.OpenEditor = editor =>
        {
            var w = new CommandEditorWindow(editor) { Owner = this };
            return w.ShowDialog() == true ? w.Result : null;
        };
    }

    /// <summary>Dark title bar to match the app (Windows 10 2004+ / 11).</summary>
    public static void UseDarkTitleBar(Window w)
    {
        try
        {
            int on = 1;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // let an open drop-down use the keyboard
        if (e.OriginalSource is DependencyObject d && FindAncestor<ComboBox>(d) is { IsDropDownOpen: true }) return;
        if (e.OriginalSource is TextBox) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        if (e.Key is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9)
        {
            int n = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 + 1 : e.Key - Key.D1 + 1;
            _vm.RunByNumber(n);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Space: _vm.Go(); break;
            case Key.Escape: _vm.StopAll(); break;
            case Key.F: _vm.FadeAll(); break;
            case Key.Up: _vm.MoveNext(-1); break;
            case Key.Down: _vm.MoveNext(+1); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.AnyActive &&
            MessageBox.Show(this, "C'è musica in riproduzione: cambiando cartella si ferma tutto. Continuare?",
                "Cue Player", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var dlg = new OpenFolderDialog { Title = "Cartella con le musiche dello spettacolo" };
        if (_vm.Folder != null) dlg.InitialDirectory = _vm.Folder;
        if (dlg.ShowDialog(this) == true) _vm.OpenFolder(dlg.FolderName);
    }

    private void CommandUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CommandViewModel c }) _vm.MoveCommand(c, -1);
    }

    private void CommandDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CommandViewModel c }) _vm.MoveCommand(c, +1);
    }

    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CueViewModel cue }) _vm.SelectNext(cue);
    }

    private void Progress_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CueViewModel cue } bar || bar.ActualWidth <= 0) return;
        cue.SeekTo(e.GetPosition(bar).X / bar.ActualWidth);
        e.Handled = true;
    }

    private void Slider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not RangeBase s) return;
        // volume 0..100 → 2% per notch; EQ ±12 dB → 0.5 dB per notch
        double step = s.Minimum < 0 ? 0.5 : 2;
        s.Value = Math.Clamp(s.Value + Math.Sign(e.Delta) * step, s.Minimum, s.Maximum);
        e.Handled = true;
    }

    private void EqSlider_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is RangeBase s) s.Value = 0;
        e.Handled = true;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_vm.AnyActive &&
            MessageBox.Show(this, "C'è musica in riproduzione. Chiudere comunque?", "Cue Player",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _vm.Dispose();
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
