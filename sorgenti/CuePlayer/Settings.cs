using System.Text.Json;

namespace CuePlayer;

public sealed class CueSettings
{
    public double Volume { get; set; } = 100;
    public bool Loop { get; set; }
    public double FadeSeconds { get; set; } = 3;
    public double[]? Eq { get; set; }
}

/// <summary>What one command does to one track.</summary>
public sealed class CueAction
{
    /// <summary>File name inside the music folder.</summary>
    public string File { get; set; } = "";
    /// <summary>Play | Pause | Stop  ("Play" = suona al Level%: starts if stopped, moves there if already playing).</summary>
    public string Kind { get; set; } = "Play";
    /// <summary>0..100, percent of the track's own volume.</summary>
    public double Level { get; set; } = 100;
    /// <summary>Transition time; 0 = at once.</summary>
    public double Seconds { get; set; } = 3;
    /// <summary>Play only: restart from the beginning.</summary>
    public bool FromStart { get; set; }
}

/// <summary>A user button: when pressed, every listed track does its action.</summary>
public sealed class ShowCommand
{
    public string Name { get; set; } = "Nuovo comando";
    public string Color { get; set; } = "#3D8BFD";
    public List<CueAction> Actions { get; set; } = new();
}

/// <summary>Per-show settings, saved as cueplayer.json inside the music folder (travels with it on a USB stick).</summary>
public sealed class ShowSettings
{
    public const string FileName = "cueplayer.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public double MasterVolume { get; set; } = 100;
    public double MasterFadeSeconds { get; set; } = 3;
    public double[]? MasterEq { get; set; }
    public Dictionary<string, CueSettings> Cues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ShowCommand> Commands { get; set; } = new();

    public static ShowSettings Load(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<ShowSettings>(File.ReadAllText(path)) ?? new();
                s.Cues = new Dictionary<string, CueSettings>(s.Cues ?? new(), StringComparer.OrdinalIgnoreCase);
                s.Commands ??= new();
                foreach (var c in s.Commands) c.Actions ??= new();
                return s;
            }
        }
        catch { }
        return new ShowSettings();
    }

    /// <summary>Returns false when the folder is read-only.</summary>
    public bool Save(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, true);
            return true;
        }
        catch { return false; }
    }
}

/// <summary>Machine settings (last folder, output device) in %AppData%\CuePlayer.</summary>
public sealed class AppSettings
{
    public string? LastFolder { get; set; }
    public string? DeviceId { get; set; }

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CuePlayer");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}
