using System.Windows.Input;
using CuePlayer.Audio;

namespace CuePlayer.ViewModels;

public sealed class CueViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    public CueViewModel(MainViewModel owner, string path, CueSettings? saved)
    {
        _owner = owner;
        FilePath = path;
        FileName = Path.GetFileName(path);
        Title = Path.GetFileNameWithoutExtension(path);
        Stamp = FileStamp(path);
        if (saved != null)
        {
            _volume = Math.Clamp(saved.Volume, 0, 100);
            _loop = saved.Loop;
            _fadeSeconds = saved.FadeSeconds > 0 ? saved.FadeSeconds : 3;
            _startAt = Math.Max(0, saved.StartAt);
            _endAt = saved.EndAt;
            _endFade = Math.Max(0, saved.EndFade);
        }

        SetStartHereCommand = new RelayCommand(() => { if (_voice != null) StartAt = Math.Round(_voice.GetStatus().Position.TotalSeconds, 1); });
        SetEndHereCommand = new RelayCommand(() => { if (_voice != null) EndAt = Math.Round(_voice.GetStatus().Position.TotalSeconds, 1); });
        ClearTrimCommand = new RelayCommand(() => { StartAt = 0; EndAt = null; EndFade = 0; });
        SetEndFadeCommand = new ParamCommand(p => { if (p is double d) EndFade = d; });

        EqBands = EqBandViewModel.Create(Equalizer.TrackBands, saved?.Eq, (band, db) =>
        {
            _voice?.SetEq(band, db);
            OnPropertyChanged(nameof(EqActive));
            _owner.ScheduleSave();
        });
        ResetEqCommand = new RelayCommand(() => { foreach (var b in EqBands) b.Gain = 0; });

        // one button: pauses while playing, plays otherwise
        PlayPauseCommand = new RelayCommand(() =>
        {
            if (Voice == null) return;
            if (Voice.GetStatus().State == PlayState.Playing) Voice.Pause();
            else { Voice.Play(); _owner.Touch(this); }
            Refresh();
        });
        StopCommand = new RelayCommand(() => { Voice?.Stop(); Refresh(); });
        RestartCommand = new RelayCommand(() => { Voice?.PlayFromStart(); _owner.Touch(this); Refresh(); });
        FadeCommand = new RelayCommand(() => { Voice?.FadeOut(FadeSeconds); Refresh(); });
    }

    public string FilePath { get; }
    public string FileName { get; }
    public string Title { get; }
    public string Stamp { get; set; }

    public ICommand PlayPauseCommand { get; }
    public ICommand ResetEqCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand FadeCommand { get; }

    public List<EqBandViewModel> EqBands { get; }
    public bool EqActive => EqBands.Any(b => b.Gain != 0);

    /// <summary>True while sound is coming out (drives the play/pause button).</summary>
    public bool IsPlaying => _state == PlayState.Playing;

    public static string FileStamp(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
        }
        catch { return ""; }
    }

    private int _number;
    public int Number { get => _number; set => Set(ref _number, value); }

    private Voice? _voice;
    public Voice? Voice
    {
        get => _voice;
        private set
        {
            if (Set(ref _voice, value)) OnPropertyChanged(nameof(IsReady));
        }
    }

    public bool IsReady => _voice != null;

    public void Attach(Voice voice)
    {
        voice.SetVolume((float)(_volume / 100.0));
        voice.Loop = _loop;
        foreach (var b in EqBands) voice.SetEq(b.Index, b.Gain);
        voice.SetTrim(TimeSpan.FromSeconds(_startAt), _endAt is { } e ? TimeSpan.FromSeconds(e) : null, _endFade);
        Voice = voice;
        ApplyNormalization();
        OnPropertyChanged(nameof(TrimStartFraction));
        OnPropertyChanged(nameof(TrimEndFraction));
        Error = null;
        DurationText = Format(voice.Duration);
        Refresh();
    }

    public Voice? Detach()
    {
        var v = _voice;
        Voice = null;
        State = PlayState.Stopped;
        StateKey = "loading";
        return v;
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value)) UpdateStateKey();
        }
    }

    private double _volume = 100;
    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(Math.Round(value), 0, 100);
            if (!Set(ref _volume, value)) return;
            _voice?.SetVolume((float)(value / 100.0));
            _owner.ScheduleSave();
        }
    }

    private bool _loop;
    public bool Loop
    {
        get => _loop;
        set
        {
            if (!Set(ref _loop, value)) return;
            if (_voice != null) _voice.Loop = value;
            _owner.ScheduleSave();
        }
    }

    private double _fadeSeconds = 3;
    public double FadeSeconds
    {
        get => _fadeSeconds;
        set
        {
            if (!Set(ref _fadeSeconds, value)) return;
            OnPropertyChanged(nameof(FadeLabel));
            _owner.ScheduleSave();
        }
    }

    public string FadeLabel => $"SFUMA {FadeSeconds:0.#}s";

    // ---------------- trim (inizio / fine) ----------------

    public static double[] EndFadeChoices { get; } = { 0, 1, 2, 3, 5, 8 };

    public ICommand SetStartHereCommand { get; }
    public ICommand SetEndHereCommand { get; }
    public ICommand ClearTrimCommand { get; }
    public ICommand SetEndFadeCommand { get; }

    private double _startAt;
    public double StartAt
    {
        get => _startAt;
        set
        {
            value = Math.Max(0, value);
            if (!Set(ref _startAt, value)) return;
            TrimChanged();
        }
    }

    private double? _endAt;
    public double? EndAt
    {
        get => _endAt;
        set
        {
            if (value is <= 0) value = null;
            if (!Set(ref _endAt, value)) return;
            TrimChanged();
        }
    }

    private double _endFade;
    public double EndFade
    {
        get => _endFade;
        set
        {
            if (!Set(ref _endFade, Math.Max(0, value))) return;
            TrimChanged();
        }
    }

    /// <summary>Editable "m:ss" text for the start point.</summary>
    public string StartText
    {
        get => _startAt > 0 ? Format(TimeSpan.FromSeconds(_startAt), true) : "";
        set { if (TryParseTime(value, out var s)) StartAt = s ?? 0; else OnPropertyChanged(); }
    }

    /// <summary>Editable "m:ss" text for the end point (empty = end of file).</summary>
    public string EndText
    {
        get => _endAt is { } e ? Format(TimeSpan.FromSeconds(e), true) : "";
        set { if (TryParseTime(value, out var s)) EndAt = s; else OnPropertyChanged(); }
    }

    public bool TrimActive => _startAt > 0 || _endAt != null || _endFade > 0;

    public string TrimSummary
    {
        get
        {
            if (!TrimActive) return "intera";
            string a = _startAt > 0 ? Format(TimeSpan.FromSeconds(_startAt), true) : "inizio";
            string b = _endAt is { } e ? Format(TimeSpan.FromSeconds(e), true) : "fine";
            return $"{a} → {b}" + (_endFade > 0 ? $", sfuma {_endFade:0.#}s" : "");
        }
    }

    /// <summary>Trim points as a fraction of the file (for the waveform dimming).</summary>
    public double TrimStartFraction => _voice != null && _voice.Duration.TotalSeconds > 0 ? _startAt / _voice.Duration.TotalSeconds : 0;
    public double TrimEndFraction => _voice != null && _voice.Duration.TotalSeconds > 0 && _endAt is { } e ? e / _voice.Duration.TotalSeconds : 1;

    private void TrimChanged()
    {
        OnPropertyChanged(nameof(TrimStartFraction));
        OnPropertyChanged(nameof(TrimEndFraction));
        _voice?.SetTrim(TimeSpan.FromSeconds(_startAt), _endAt is { } e ? TimeSpan.FromSeconds(e) : null, _endFade);
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(EndText));
        OnPropertyChanged(nameof(TrimActive));
        OnPropertyChanged(nameof(TrimSummary));
        Refresh();
        _owner.ScheduleSave();
    }

    /// <summary>Accepts "75", "1:15", "1:15.5", "" (= none).</summary>
    public static bool TryParseTime(string? text, out double? seconds)
    {
        seconds = null;
        text = (text ?? "").Trim().Replace(',', '.');
        if (text.Length == 0) return true;
        var parts = text.Split(':');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            double total = 0;
            foreach (var p in parts) total = total * 60 + double.Parse(p, inv);
            if (total < 0) return false;
            seconds = total;
            return true;
        }
        catch { return false; }
    }

    // ---------------- analysis: waveform + loudness ----------------

    private AudioAnalysis? _analysis;
    /// <summary>Waveform overview (null until analysed).</summary>
    public float[]? Peaks => _analysis?.Peaks;
    public bool HasPeaks => _analysis != null;

    public void SetAnalysis(AudioAnalysis a)
    {
        _analysis = a;
        OnPropertyChanged(nameof(Peaks));
        OnPropertyChanged(nameof(HasPeaks));
        ApplyNormalization();
    }

    /// <summary>Applies (or removes) the "volumi uniformati" gain according to the feature switch.</summary>
    public void ApplyNormalization()
    {
        double db = _owner.Features.Normalize && _analysis != null ? _analysis.NormalizationDb : 0;
        _voice?.SetNormalization((float)Math.Pow(10, db / 20));
        NormalizationText = _owner.Features.Normalize && _analysis != null ? $"{db:+0.0;-0.0;0} dB" : "";
    }

    private string _normalizationText = "";
    /// <summary>e.g. "+3.5 dB" — shown next to the volume when normalisation is on.</summary>
    public string NormalizationText { get => _normalizationText; private set => Set(ref _normalizationText, value); }

    // ---------------- headphone preview ----------------

    private bool _isPreviewing;
    public bool IsPreviewing { get => _isPreviewing; set => Set(ref _isPreviewing, value); }

    public ICommand PreviewCommand => _previewCommand ??= new RelayCommand(() => _owner.TogglePreview(this));
    private ICommand? _previewCommand;

    // ---------------- end warning ----------------

    private string _remainingText = "";
    /// <summary>Big countdown for Show mode, e.g. "-0:08".</summary>
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }

    private bool _isEnding;
    /// <summary>Playing, not looping, less than 10 s to the end: the row blinks.</summary>
    public bool IsEnding { get => _isEnding; private set => Set(ref _isEnding, value); }

    private int _levelPercent;
    public int LevelPercent { get => _levelPercent; private set => Set(ref _levelPercent, value); }

    // ---------------- state ----------------

    private bool _isNext;
    public bool IsNext { get => _isNext; set => Set(ref _isNext, value); }

    private PlayState _state;
    public PlayState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value)) OnPropertyChanged(nameof(IsPlaying));
        }
    }

    public bool IsActive => _state != PlayState.Stopped;

    private VoiceStatus _status;

    /// <summary>loading | error | stopped | playing | paused | fading | ducking | ducked | fadingin — drives the row colour.</summary>
    private string _stateKey = "loading";
    public string StateKey { get => _stateKey; private set => Set(ref _stateKey, value); }

    private string _stateText = "caricamento…";
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    private string _timeText = "";
    public string TimeText { get => _timeText; private set => Set(ref _timeText, value); }

    private string _durationText = "";
    public string DurationText { get => _durationText; private set => Set(ref _durationText, value); }

    public void SeekTo(double fraction)
    {
        if (_voice == null) return;
        _voice.Seek(TimeSpan.FromSeconds(_voice.Duration.TotalSeconds * Math.Clamp(fraction, 0, 1)));
        Refresh();
    }

    /// <summary>Polled by the UI timer.</summary>
    public void Refresh()
    {
        if (_voice == null)
        {
            UpdateStateKey();
            return;
        }
        var s = _voice.GetStatus();
        _status = s;
        if (s.Failed && _error == null) Error = "errore di lettura del file";
        State = s.State;
        OnPropertyChanged(nameof(IsActive));

        var dur = _voice.Duration;
        var pos = s.Position;
        var end = s.End > TimeSpan.Zero ? s.End : dur;
        var left = end - pos;
        Progress = dur.TotalSeconds > 0 ? pos.TotalSeconds / dur.TotalSeconds : 0;
        TimeText = s.State == PlayState.Stopped
            ? (TrimActive ? Format(end - s.Start) : DurationText)
            : $"{Format(pos)}  /  -{Format(left)}";
        RemainingText = s.State == PlayState.Stopped ? Format(end - s.Start) : "-" + Format(left);
        IsEnding = s.State == PlayState.Playing && !_loop && left.TotalSeconds <= 10 && _owner.Features.EndWarning;
        LevelPercent = (int)Math.Round(s.Level * 100);
        UpdateStateKey();
    }

    private void UpdateStateKey()
    {
        if (_error != null) { StateKey = "error"; StateText = _error; return; }
        if (_voice == null) { StateKey = "loading"; StateText = "caricamento…"; return; }
        var s = _status;
        int pct = (int)Math.Round(s.Level * 100);
        (StateKey, StateText) = _state switch
        {
            PlayState.Playing when s.FadingOut => ("fading", "IN SFUMATURA"),
            PlayState.Playing when s.Ducking => ("ducking", $"SI ABBASSA · {pct}%"),
            PlayState.Playing when s.FadingIn => ("fadingin", $"IN ENTRATA · {pct}%"),
            PlayState.Playing when s.Level < 0.97f => ("ducked", $"SOTTOFONDO · {pct}%"),
            PlayState.Playing => ("playing", "IN RIPRODUZIONE"),
            PlayState.Paused => ("paused", "IN PAUSA"),
            _ => ("stopped", ""),
        };
    }

    public CueSettings ToSettings() => new()
    {
        Volume = _volume,
        Loop = _loop,
        FadeSeconds = _fadeSeconds,
        Eq = EqActive ? EqBands.Select(b => b.Gain).ToArray() : null,
        StartAt = _startAt,
        EndAt = _endAt,
        EndFade = _endFade,
    };

    private static string Format(TimeSpan t, bool tenths = false)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        string s = t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        if (tenths && t.Milliseconds >= 100) s += "." + (t.Milliseconds / 100);
        return s;
    }
}
