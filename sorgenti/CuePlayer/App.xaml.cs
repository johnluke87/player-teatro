using System.Windows;
using System.Windows.Threading;

namespace CuePlayer;

public partial class App : Application
{
    public static string? StartupFolder { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // never crash in the middle of a show: log and keep going
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log(a.Exception); a.SetObserved(); };

        if (e.Args.Length > 0 && Directory.Exists(e.Args[0])) StartupFolder = e.Args[0];
        base.OnStartup(e);
        new MainWindow().Show();
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        if (MainWindow?.DataContext is ViewModels.MainViewModel vm)
            vm.Status = "⚠ Errore interno (registrato), l'audio continua: " + e.Exception.Message;
    }

    public static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.AppendAllText(Path.Combine(AppSettings.Dir, "errori.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { }
    }
}
