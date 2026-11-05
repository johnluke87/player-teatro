using System.Windows.Input;
using System.Windows.Media;

namespace CuePlayer.ViewModels;

/// <summary>
/// Play = parte (starts, or resumes; if already playing just goes to the level);
/// Volume = only changes the level of a track that is already playing (si abbassa / torna su);
/// Pause; Stop.
/// </summary>
public enum ActionKind { None, Play, Volume, Pause, Stop }

public static class CommandText
{
    public static string TitleOf(string file) => Path.GetFileNameWithoutExtension(file);

    private static string Short(string s) => s.Length > 24 ? s[..23] + "…" : s;

    public static string Secs(double s) => s <= 0 ? "subito" : $"in {s:0.#}s";

    /// <summary>One plain-language line per track, e.g. "▶ MusicaB parte" / "🔉 MusicaA → volume 20% in 2s".</summary>
    public static string Line(CueAction a)
    {
        string t = Short(TitleOf(a.File));
        string lvl = a.Level < 100 ? $" al {a.Level:0}%" : "";
        string fade = a.Seconds > 0 ? $" (sfumando in {a.Seconds:0.#}s)" : "";
        return Parse(a.Kind) switch
        {
            ActionKind.Play => $"▶ {t} parte{lvl}{fade}" + (a.FromStart ? " · da capo" : ""),
            ActionKind.Volume => $"🔉 {t} → volume {a.Level:0}%" + (a.Seconds > 0 ? $" in {a.Seconds:0.#}s" : " subito"),
            ActionKind.Stop => $"⏹ {t} stop{fade}",
            ActionKind.Pause => $"⏸ {t} pausa",
            _ => "",
        };
    }

    public static string Summary(IEnumerable<CueAction> actions,
        string empty = "non fa ancora niente — premi ✎ per impostarlo")
    {
        var lines = actions.Where(a => Parse(a.Kind) != ActionKind.None).Select(Line).ToList();
        return lines.Count == 0 ? empty : string.Join("\n", lines);
    }

    public static ActionKind Parse(string? kind) =>
        Enum.TryParse<ActionKind>(kind, true, out var k) ? k : ActionKind.None;
}

/// <summary>One pad in the COMANDI panel.</summary>
public sealed class CommandViewModel : ObservableObject
{
    public CommandViewModel(MainViewModel owner, ShowCommand model)
    {
        Model = model;
        RunCommand = new RelayCommand(() => owner.Run(this));
        EditCommand = new RelayCommand(() => owner.Edit(this));
    }

    public ShowCommand Model { get; private set; }
    public ICommand RunCommand { get; }
    public ICommand EditCommand { get; }

    public string Name => Model.Name;
    /// <summary>Act / scene header this pad sits under.</summary>
    public string Group => string.IsNullOrWhiteSpace(Model.Group) ? "Senza atto" : Model.Group.Trim();
    public bool AutoNext => Model.AutoNext;
    public string AutoNextText => Model.AutoNext ? $"poi il prossimo da solo dopo {Model.AutoNextSeconds:0.#} s" : "";
    public string Note => Model.Note ?? "";
    public bool HasNote => !string.IsNullOrWhiteSpace(Model.Note);
    public string Summary => CommandText.Summary(Model.Actions);

    public Brush ColorBrush
    {
        get
        {
            try { return (Brush)new BrushConverter().ConvertFromString(Model.Color)!; }
            catch { return Brushes.SteelBlue; }
        }
    }

    private int _number;
    public int Number
    {
        get => _number;
        set { if (Set(ref _number, value)) OnPropertyChanged(nameof(KeyHint)); }
    }

    /// <summary>Keyboard shortcut shown on the pad (1-9).</summary>
    public string KeyHint => _number <= 9 ? _number.ToString() : "";

    private bool _isNext;
    public bool IsNext { get => _isNext; set => Set(ref _isNext, value); }

    private bool _isLast;
    public bool IsLast { get => _isLast; set => Set(ref _isLast, value); }

    public void Update(ShowCommand model)
    {
        Model = model;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Group));
        OnPropertyChanged(nameof(AutoNext));
        OnPropertyChanged(nameof(AutoNextText));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ColorBrush));
    }

    public void RefreshSummary() => OnPropertyChanged(nameof(Summary));
}
