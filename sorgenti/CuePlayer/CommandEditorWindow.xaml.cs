using System.Windows;
using CuePlayer.ViewModels;

namespace CuePlayer;

public partial class CommandEditorWindow : Window
{
    /// <summary>"save", "delete" or null.</summary>
    public string? Result { get; private set; }

    public CommandEditorWindow(CommandEditorViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        DeleteBtn.Visibility = vm.IsNew ? Visibility.Collapsed : Visibility.Visible;
        SourceInitialized += (_, _) => MainWindow.UseDarkTitleBar(this);
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = "save";
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Eliminare questo comando?", "Cue Player",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Result = "delete";
        DialogResult = true;
    }
}
