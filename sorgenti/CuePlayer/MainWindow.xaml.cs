using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CuePlayer.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MenuItem = System.Windows.Controls.MenuItem;

namespace CuePlayer;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private ShowWindow? _show;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(Dispatcher);
        DataContext = _vm;
        Loaded += (_, _) => { _vm.Initialize(App.StartupFolder); Keyboard.Focus(this); };
        // the transport buttons never take focus: make sure Space/1-9/Esc always have somewhere to go
        Activated += (_, _) => EnsureKeyboardFocus(this);
        PreviewMouseUp += (_, _) => EnsureKeyboardFocus(this);
        _vm.OpenEditor = editor =>
        {
            var w = new CommandEditorWindow(editor) { Owner = (Window?)_show ?? this };
            return w.ShowDialog() == true ? w.Result : null;
        };
    }

    public const string AppName = "RTC Stage";

    // ---------------- keyboard (shared with Show mode) ----------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 && _vm.Features.ShowMode) { OpenShowMode(); e.Handled = true; return; }
        HandleShortcut(_vm, e);
    }

    /// <summary>Space = GO, 1-9 = command, Esc = stop all, F = fade all, ↑↓ = next.</summary>
    public static void HandleShortcut(MainViewModel vm, KeyEventArgs e)
    {
        // let an open drop-down or a text field use the keyboard
        if (e.OriginalSource is DependencyObject d && FindAncestor<ComboBox>(d) is { IsDropDownOpen: true }) return;
        if (e.OriginalSource is System.Windows.Controls.TextBox) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        if (e.Key is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9)
        {
            int n = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 + 1 : e.Key - Key.D1 + 1;
            vm.RunByNumber(n);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Space: vm.Go(); break;
            case Key.Escape: vm.StopAll(); break;
            case Key.F: vm.FadeAll(); break;
            case Key.Up: vm.MoveNext(-1); break;
            case Key.Down: vm.MoveNext(+1); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------------- toolbar ----------------

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.AnyActive &&
            MessageBox.Show(this, "C'è musica in riproduzione: cambiando cartella si ferma tutto. Continuare?",
                AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var dlg = new OpenFolderDialog { Title = "Cartella con le musiche dello spettacolo" };
        if (_vm.Folder != null) dlg.InitialDirectory = _vm.Folder;
        if (dlg.ShowDialog(this) == true) _vm.OpenFolder(dlg.FolderName);
    }

    private void ShowMode_Click(object sender, RoutedEventArgs e) => OpenShowMode();

    private void OpenShowMode()
    {
        if (_show != null) { _show.Activate(); return; }
        _show = new ShowWindow(_vm);
        _show.Closed += (_, _) => { _show = null; Activate(); };
        _show.Show();
    }

    private void Features_Click(object sender, RoutedEventArgs e)
    {
        new FeaturesWindow(_vm.Features) { Owner = this }.ShowDialog();
    }

    /// <summary>Opens the duration menu of a split SFUMA button (per track or "Sfuma tutto").</summary>
    private void FadeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement b) return;
        var cue = b.DataContext as CueViewModel;
        double current = cue?.FadeSeconds ?? _vm.MasterFadeSeconds;
        var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Durata della sfumata", IsEnabled = false });
        foreach (var s in _vm.FadeChoices)
        {
            var item = new MenuItem { Header = $"{s:0.#} secondi", IsCheckable = true, IsChecked = Math.Abs(s - current) < 0.01 };
            double value = s;
            item.Click += (_, _) =>
            {
                if (cue != null) cue.FadeSeconds = value;
                else _vm.MasterFadeSeconds = value;
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---------------- commands panel ----------------

    private void CommandUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CommandViewModel c }) _vm.MoveCommand(c, -1);
    }

    private void CommandDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CommandViewModel c }) _vm.MoveCommand(c, +1);
    }

    // ---------------- tracks ----------------

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

    /// <summary>Enter in a time field commits it right away.</summary>
    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not System.Windows.Controls.TextBox tb) return;
        tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
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
            MessageBox.Show(this, "C'è musica in riproduzione. Chiudere comunque?", AppName,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _show?.Close();
        _vm.Dispose();
    }

    /// <summary>If nothing inside the window has keyboard focus, give it to the window itself.</summary>
    internal static void EnsureKeyboardFocus(Window w)
    {
        if (Keyboard.FocusedElement is DependencyObject f && Window.GetWindow(f) == w) return;
        w.Dispatcher.BeginInvoke(() => { if (w.IsActive) Keyboard.Focus(w); });
    }

    internal static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
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
