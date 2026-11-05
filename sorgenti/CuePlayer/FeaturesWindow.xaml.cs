using System.Windows;
using CuePlayer.ViewModels;
using Wpf.Ui.Controls;

namespace CuePlayer;

public partial class FeaturesWindow : FluentWindow
{
    public FeaturesWindow(FeaturesViewModel features)
    {
        InitializeComponent();
        DataContext = features;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
