using System.Text.Json;

namespace CuePlayer;

public sealed class CueSettings
{
    public double Volume { get; set; } = 100;
    public bool Loop { get; set; }
    public double FadeSeconds { get; set; } = 3;
    public double[]? Eq { get; set; }
    /// <summary>Trim start in seconds (0 = beginning of the file).</summary>
    public double StartAt { get; set; }
    /// <summary>Trim end in seconds (null = end of the file).</summary>
    public double? EndAt { get; set; }
    /// <summary>Automatic fade-out before the trim end, seconds (0 = none).</summary>
    public double EndFade { get; set; }
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
    /// <summary>Director's note: when to fire it (e.g. the cue line). Shown big in Show mode.</summary>
    public string Note { get; set; } = "";
    /// <summary>Act / scene this command belongs to ("" = none). Commands are grouped under it.</summary>
    public string Group { get; set; } = "";
    /// <summary>After running, fire the next command by itself after <see cref="AutoNextSeconds"/>.</summary>
    public bool AutoNext { get; set; }
    public double AutoNextSeconds { get; set; } = 5;
    public List<CueAction> Actions { get; set; } = new();
}

/// <summary>Optional features, switched on in the "Funzioni" page. Off = hidden, so the main view stays simple.</summary>
public sealed class FeatureFlags
{
    public bool ShowMode { get; set; } = true;
    public bool Notes { get; set; } = true;
    public bool Trim { get; set; } = true;
    public bool EndWarning { get; set; } = true;
    public bool TrackEq { get; set; } = true;
    public bool MasterEq { get; set; } = true;
    public bool Preview { get; set; }
    public bool Waveform { get; set; } = true;
    public bool Chain { get; set; } = true;
    public bool Undo { get; set; } = true;
    public bool Normalize { get; set; }
    public bool Limiter { get; set; } = true;
    public bool Acts { get; set; } = true;
    public bool PrintList { get; set; } = true;
    public bool Backup { get; set; } = true;
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
    public FeatureFlags Features { get; set; } = new();
    /// <summary>Headphone (pre-listen) output; null = none chosen yet.</summary>
    public string? PreviewDeviceId { get; set; }

    /// <summary>%AppData%\CuePlayer (name kept from v1 so existing settings survive). Tests redirect it with RTCSTAGE_SETTINGS_DIR.</summary>
    public static string Dir => Environment.GetEnvironmentVariable("RTCSTAGE_SETTINGS_DIR") is { Length: > 0 } d
        ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CuePlayer");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
                s.Features ??= new();
                return s;
            }
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
