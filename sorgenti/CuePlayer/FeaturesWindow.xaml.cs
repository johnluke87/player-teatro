using System.Windows;
using CuePlayer.ViewModels;
using Wpf.Ui.Controls;

namespace CuePlayer;

public partial class FeaturesWindow : FluentWindow
{
    public sealed record UpcomingFeature(string Glyph, string Title, string Description);

    public FeaturesWindow(FeaturesViewModel features)
    {
        InitializeComponent();
        DataContext = features;
        Upcoming.ItemsSource = new[]
        {
            new UpcomingFeature("", "Pre-ascolto in cuffia", "Ascolta un brano su una seconda uscita (le cuffie del tecnico) senza farlo sentire in sala."),
            new UpcomingFeature("", "Forma d'onda", "La barra di avanzamento mostra la forma del suono: vedi dove sono silenzi e picchi."),
            new UpcomingFeature("", "Comandi in catena", "«Dopo 5 secondi esegui da solo il prossimo comando»."),
            new UpcomingFeature("", "Annulla l'ultimo comando", "Torna alla situazione di prima con un tasto."),
            new UpcomingFeature("", "Volumi uniformati", "Il programma misura i file e li porta tutti allo stesso volume percepito."),
            new UpcomingFeature("", "Limitatore", "Evita la distorsione se più musiche si sommano troppo forte."),
            new UpcomingFeature("", "Atti e scene", "Raggruppa i comandi per atto, con gruppi richiudibili."),
            new UpcomingFeature("", "Stampa della scaletta", "PDF con l'elenco dei comandi e delle note di regia, da dare alla regia."),
            new UpcomingFeature("", "Backup dello spettacolo", "Un unico file zip con musiche e impostazioni, da portare su un altro PC."),
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
