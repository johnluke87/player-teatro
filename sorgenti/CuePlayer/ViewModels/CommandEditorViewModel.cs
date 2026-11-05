using System.Windows.Input;

namespace CuePlayer.ViewModels;

public sealed record KindOption(ActionKind Kind, string Label);

/// <summary>One track inside the command editor.</summary>
public sealed class ActionRowViewModel : ObservableObject
{
    private readonly Action _changed;

    public ActionRowViewModel(string file, bool missing, CueAction? a, Action changed)
    {
        File = file;
        Title = CommandText.TitleOf(file) + (missing ? "  (file mancante)" : "");
        Missing = missing;
        _changed = changed;
        SetKindCommand = new ParamCommand(p => { if (p is KindOption k) Choose(k.Kind); });
        SetSecondsCommand = new ParamCommand(p => { if (p is double s) Seconds = s; });
        SetLevelCommand = new ParamCommand(p => { if (p is double l) Level = l; });
        if (a != null)
        {
            _kind = CommandText.Parse(a.Kind);
            _level = Math.Clamp(a.Level, 0, 100);
            _seconds = Math.Max(0, a.Seconds);
            _fromStart = a.FromStart;
        }
    }

    public string File { get; }
    public string Title { get; }
    public bool Missing { get; }
    public ICommand SetKindCommand { get; }
    public ICommand SetSecondsCommand { get; }
    public ICommand SetLevelCommand { get; }

    private ActionKind _kind = ActionKind.None;
    public ActionKind Kind
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value)) return;
            OnPropertyChanged(nameof(IsPlay));
            OnPropertyChanged(nameof(IsLevel));
            OnPropertyChanged(nameof(IsTimed));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(Hint));
            _changed();
        }
    }

    /// <summary>Clicked a choice: sensible starting values for it (user can change them after).</summary>
    private void Choose(ActionKind k)
    {
        if (k == _kind) return;
        switch (k)
        {
            case ActionKind.Play: Level = 100; Seconds = 0; break;            // parte subito a pieno volume
            case ActionKind.Volume: Level = 20; Seconds = 2; break;           // si abbassa in 2 s
            case ActionKind.Stop: Seconds = 0; break;                         // stop secco
        }
        Kind = k;
    }

    public bool IsPlay => _kind == ActionKind.Play;
    public bool IsLevel => _kind is ActionKind.Play or ActionKind.Volume;
    public bool IsTimed => _kind is ActionKind.Play or ActionKind.Volume or ActionKind.Stop;
    public bool IsActive => _kind != ActionKind.None;

    /// <summary>Short explanation under the choices.</summary>
    public string Hint => _kind switch
    {
        ActionKind.Play => "parte (se era in pausa riprende); se suona già, va solo al volume indicato",
        ActionKind.Volume => "solo se sta già suonando: la abbassa o la riporta su (100% = volume pieno)",
        ActionKind.Stop => "si ferma e torna all'inizio",
        ActionKind.Pause => "si ferma dov'è; con ▶ Parte riprende da lì",
        _ => "",
    };

    private double _level = 100;
    public double Level
    {
        get => _level;
        set { value = Math.Round(Math.Clamp(value, 0, 100) / 5) * 5; if (Set(ref _level, value)) _changed(); }
    }

    private double _seconds = 3;
    public double Seconds
    {
        get => _seconds;
        set { if (value >= 0 && Set(ref _seconds, value)) _changed(); }
    }

    private bool _fromStart;
    public bool FromStart
    {
        get => _fromStart;
        set { if (Set(ref _fromStart, value)) _changed(); }
    }

    public CueAction? ToAction() => _kind == ActionKind.None
        ? null
        : new CueAction { File = File, Kind = _kind.ToString(), Level = _level, Seconds = _seconds, FromStart = _fromStart };
}

public sealed class CommandEditorViewModel : ObservableObject
{
    public static KindOption[] Kinds { get; } =
    {
        new(ActionKind.None, "— non toccare"),
        new(ActionKind.Play, "▶ Parte"),
        new(ActionKind.Volume, "🔉 Volume"),
        new(ActionKind.Pause, "⏸ Pausa"),
        new(ActionKind.Stop, "⏹ Stop"),
    };

    public static double[] SecondsChoices { get; } = { 0, 1, 2, 3, 5, 10 };
    public static double[] LevelChoices { get; } = { 10, 20, 30, 50, 70, 100 };

    public static string[] Colors { get; } =
        { "#3D8BFD", "#25A866", "#E8712A", "#9B5DE5", "#2BB3A8", "#D93A40", "#E9A23B", "#7A8594" };

    private readonly MainViewModel _owner;

    public CommandEditorViewModel(MainViewModel owner, ShowCommand source, bool isNew)
    {
        _owner = owner;
        IsNew = isNew;
        _name = source.Name;
        _color = source.Color;

        var byFile = source.Actions.GroupBy(a => a.File, StringComparer.OrdinalIgnoreCase)
                                   .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        foreach (var cue in owner.Cues)
        {
            byFile.TryGetValue(cue.FileName, out var a);
            Rows.Add(new ActionRowViewModel(cue.FileName, false, a, Changed));
            byFile.Remove(cue.FileName);
        }
        // actions on files no longer in the folder stay visible (kept unless set to "non toccare")
        foreach (var a in byFile.Values) Rows.Add(new ActionRowViewModel(a.File, true, a, Changed));

        SnapshotCommand = new RelayCommand(TakeSnapshot);
        SetColorCommand = new ParamCommand(p => { if (p is string c) Color = c; });
        TryCommand = new RelayCommand(() => _owner.Run(ToModel()));
        AllSecondsCommand = new ParamCommand(p =>
        {
            if (p is double s) foreach (var r in Rows.Where(r => r.IsTimed)) r.Seconds = s;
        });
    }

    public bool IsNew { get; }
    public string WindowTitle => IsNew ? "Nuovo comando" : "Modifica comando";
    public List<ActionRowViewModel> Rows { get; } = new();

    public ICommand SnapshotCommand { get; }
    public ICommand SetColorCommand { get; }
    public ICommand TryCommand { get; }
    public ICommand AllSecondsCommand { get; }

    private string _name;
    public string Name { get => _name; set { if (Set(ref _name, value)) OnPropertyChanged(nameof(CanSave)); } }

    public bool CanSave => !string.IsNullOrWhiteSpace(_name);

    private string _color;
    public string Color { get => _color; set => Set(ref _color, value); }

    public string Summary => CommandText.Summary(Rows.Select(r => r.ToAction()).OfType<CueAction>(),
        "ancora niente: qui sopra clicca «▶ Parte», «🔉 Volume», «⏸ Pausa» o «⏹ Stop» sulle tracce che devono fare qualcosa");

    private void Changed() => OnPropertyChanged(nameof(Summary));

    /// <summary>Copies what is playing now (and at which level) into the command.</summary>
    private void TakeSnapshot()
    {
        foreach (var row in Rows)
        {
            var cue = _owner.Cues.FirstOrDefault(c => c.FileName.Equals(row.File, StringComparison.OrdinalIgnoreCase));
            var st = cue?.Voice?.GetStatus();
            if (st is { State: Audio.PlayState.Playing } s)
            {
                row.Kind = ActionKind.Play;
                row.Level = Math.Round(s.Level * 100 / 5) * 5;
                row.Seconds = 2;
            }
            else row.Kind = ActionKind.None;
        }
    }

    public ShowCommand ToModel() => new()
    {
        Name = string.IsNullOrWhiteSpace(_name) ? "Comando" : _name.Trim(),
        Color = _color,
        Actions = Rows.Select(r => r.ToAction()).OfType<CueAction>().ToList(),
    };
}
