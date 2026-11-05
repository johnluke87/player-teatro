using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using CuePlayer.Audio;

namespace CuePlayer.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _ui;
    private readonly AudioEngine _engine = new();
    private readonly DeviceWatcher _devices = new();
    private readonly AppSettings _app = AppSettings.Load();
    private readonly DispatcherTimer _tick, _saveTimer, _rescanTimer, _deviceTimer, _recoverTimer;
    private FileSystemWatcher? _watcher;
    private ShowSettings _show = new();
    private int _generation;
    private bool _suppressDeviceChange;
    private DateTime _lastGo = DateTime.MinValue;

    public ObservableCollection<CueViewModel> Cues { get; } = new();
    public ObservableCollection<DeviceItem> Devices { get; } = new();
    public double[] FadeChoices { get; } = { 1, 2, 3, 4, 5, 8, 10, 15, 20, 30 };

    public ICommand GoCommand { get; }
    public ICommand StopAllCommand { get; }
    public ICommand FadeAllCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand ResetMasterEqCommand { get; }

    public List<EqBandViewModel> MasterEqBands { get; }
    public bool MasterEqActive => MasterEqBands.Any(b => b.Gain != 0);

    private string _saveText = "";
    /// <summary>"✓ salvato 21:03" — visible proof that settings reached cueplayer.json.</summary>
    public string SaveText { get => _saveText; private set => Set(ref _saveText, value); }

    // ---------------- commands (user buttons) ----------------

    public ObservableCollection<CommandViewModel> Commands { get; } = new();
    public ICommand NewCommandCommand { get; private set; } = null!;

    /// <summary>Set by the window: shows the editor; returns "save", "delete" or null (cancel).</summary>
    public Func<CommandEditorViewModel, string?>? OpenEditor { get; set; }

    public bool HasCommands => Commands.Count > 0;

    private CommandViewModel? _nextCommand;
    public CommandViewModel? NextCommand
    {
        get => _nextCommand;
        set
        {
            var old = _nextCommand;
            if (!Set(ref _nextCommand, value)) return;
            if (old != null) old.IsNext = false;
            if (value != null) value.IsNext = true;
            OnPropertyChanged(nameof(NextLabel));
            OnPropertyChanged(nameof(NextTitle));
            OnPropertyChanged(nameof(NextSummary));
        }
    }

    /// <summary>GO walks the commands when there are any, otherwise the tracks.</summary>
    public string NextLabel => HasCommands ? "PROSSIMO COMANDO  (↑ ↓ per cambiarlo)" : "PROSSIMA TRACCIA  (↑ ↓ per cambiarla)";

    public string NextSummary => HasCommands && _nextCommand != null
        ? _nextCommand.Summary.Replace("\n", "   ")
        : "";

    public void Run(CommandViewModel cmd)
    {
        Run(cmd.Model);
        foreach (var c in Commands) c.IsLast = c == cmd;
        int i = Commands.IndexOf(cmd);
        NextCommand = i >= 0 && i + 1 < Commands.Count ? Commands[i + 1] : null;
    }

    /// <summary>Every listed track does its action at the same moment.</summary>
    public void Run(ShowCommand cmd)
    {
        foreach (var a in cmd.Actions)
        {
            var cue = Cues.FirstOrDefault(c => c.FileName.Equals(a.File, StringComparison.OrdinalIgnoreCase));
            if (cue?.Voice == null) continue;
            Apply(cue.Voice, a);
            cue.Refresh();
        }
    }

    /// <summary>What one command action does to one track.</summary>
    public static void Apply(Voice v, CueAction a)
    {
        var state = v.GetStatus().State;
        double t = Math.Max(0, a.Seconds);
        double level = Math.Clamp(a.Level, 0, 100) / 100.0;
        switch (CommandText.Parse(a.Kind))
        {
            case ActionKind.Play:
                // starts if stopped/paused, otherwise moves to the level from where it is
                if (a.FromStart && state != PlayState.Stopped) v.PlayFromStart();
                if (level <= 0) { if (state == PlayState.Playing) v.FadeOut(Math.Max(t, 0.012), crossfade: true); }
                else v.FadeTo(level, Math.Max(t, 0.012));
                break;
            case ActionKind.Volume:
                // only a track that is already playing; 0% = fades out and stops
                if (state == PlayState.Playing) v.FadeTo(level, Math.Max(t, 0.012));
                break;
            case ActionKind.Stop:
                if (state == PlayState.Playing && t > 0.05) v.FadeOut(t, crossfade: true);
                else v.Stop();
                break;
            case ActionKind.Pause:
                v.Pause();
                break;
        }
    }

    public void NewCommand()
    {
        var vm = new CommandEditorViewModel(this, new ShowCommand
        {
            Name = $"Comando {Commands.Count + 1}",
            Color = CommandEditorViewModel.Colors[Commands.Count % CommandEditorViewModel.Colors.Length],
        }, isNew: true);
        if (OpenEditor?.Invoke(vm) != "save") return;
        var c = new CommandViewModel(this, vm.ToModel());
        Commands.Add(c);
        CommandsChanged();
        if (_nextCommand == null) NextCommand = c;
    }

    public void Edit(CommandViewModel cmd)
    {
        var vm = new CommandEditorViewModel(this, cmd.Model, isNew: false);
        switch (OpenEditor?.Invoke(vm))
        {
            case "save":
                cmd.Update(vm.ToModel());
                CommandsChanged();
                break;
            case "delete":
                int i = Commands.IndexOf(cmd);
                Commands.Remove(cmd);
                if (_nextCommand == cmd) NextCommand = i < Commands.Count ? Commands[i] : Commands.LastOrDefault();
                CommandsChanged();
                break;
        }
    }

    public void MoveCommand(CommandViewModel cmd, int delta)
    {
        int i = Commands.IndexOf(cmd), j = i + delta;
        if (i < 0 || j < 0 || j >= Commands.Count) return;
        Commands.Move(i, j);
        CommandsChanged();
    }

    private void CommandsChanged()
    {
        for (int i = 0; i < Commands.Count; i++) Commands[i].Number = i + 1;
        if (_nextCue != null) _nextCue.IsNext = !HasCommands;
        OnPropertyChanged(nameof(HasCommands));
        OnPropertyChanged(nameof(NextLabel));
        OnPropertyChanged(nameof(NextTitle));
        OnPropertyChanged(nameof(NextSummary));
        ScheduleSave();
    }

    public void RunByNumber(int n)
    {
        if (n >= 1 && n <= Commands.Count) Run(Commands[n - 1]);
    }

    public MainViewModel(Dispatcher ui)
    {
        _ui = ui;
        _tick = new DispatcherTimer(DispatcherPriority.Render, ui) { Interval = TimeSpan.FromMilliseconds(40) };
        _tick.Tick += (_, _) => OnTick();
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(0.6) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveShow(); };
        _rescanTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
        _rescanTimer.Tick += (_, _) => { _rescanTimer.Stop(); Rescan(); };
        _deviceTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(700) };
        _deviceTimer.Tick += (_, _) => { _deviceTimer.Stop(); OnDevicesChanged(); };
        _recoverTimer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(500) };
        _recoverTimer.Tick += (_, _) => { _recoverTimer.Stop(); RecoverOutput(); };

        MasterEqBands = EqBandViewModel.Create(Equalizer.MasterBands, null, (band, db) =>
        {
            _engine.SetMasterEq(band, db);
            OnPropertyChanged(nameof(MasterEqActive));
            ScheduleSave();
        });
        ResetMasterEqCommand = new RelayCommand(() => { foreach (var b in MasterEqBands) b.Gain = 0; });

        GoCommand = new RelayCommand(() => Go());
        NewCommandCommand = new RelayCommand(NewCommand);
        StopAllCommand = new RelayCommand(StopAll);
        FadeAllCommand = new RelayCommand(FadeAll);
        RescanCommand = new RelayCommand(Rescan);

        _engine.OutputFailed += (_, ex) =>
        {
            Status = "⚠ Uscita audio interrotta" + (ex != null ? $" ({ex.Message})" : "") + " — ripristino in corso…";
            _recoverTimer.Start();
        };
        _devices.DevicesChanged += () => _ui.BeginInvoke(() => { _deviceTimer.Stop(); _deviceTimer.Start(); });
        _devices.DefaultChanged += () => _ui.BeginInvoke(() => { _deviceTimer.Stop(); _deviceTimer.Start(); });
    }

    public void Initialize(string? startupFolder)
    {
        _suppressDeviceChange = true;
        foreach (var d in _devices.List()) Devices.Add(d);
        _selectedDevice = Devices.FirstOrDefault(d => d.Id == _app.DeviceId) ?? Devices[0];
        OnPropertyChanged(nameof(SelectedDevice));
        _suppressDeviceChange = false;
        StartOutput();

        _tick.Start();
        PreventSleep(true);

        var folder = startupFolder ?? _app.LastFolder;
        if (folder != null && Directory.Exists(folder)) OpenFolder(folder);
    }

    // ---------------- properties ----------------

    private string? _folder;
    public string? Folder { get => _folder; private set { if (Set(ref _folder, value)) OnPropertyChanged(nameof(HasFolder)); } }
    public bool HasFolder => _folder != null;

    private bool _hasCues;
    public bool HasCues { get => _hasCues; private set => Set(ref _hasCues, value); }

    private string _status = "Pronto";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _outputText = "";
    public string OutputText { get => _outputText; private set => Set(ref _outputText, value); }

    private DeviceItem? _selectedDevice;
    public DeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (value == null || !Set(ref _selectedDevice, value) || _suppressDeviceChange) return;
            _app.DeviceId = value.Id;
            _app.Save();
            StartOutput();
        }
    }

    private double _masterVolume = 100;
    public double MasterVolume
    {
        get => _masterVolume;
        set
        {
            value = Math.Clamp(Math.Round(value), 0, 100);
            if (!Set(ref _masterVolume, value)) return;
            _engine.SetMasterVolume((float)(value / 100));
            ScheduleSave();
        }
    }

    private double _masterFadeSeconds = 3;
    public double MasterFadeSeconds
    {
        get => _masterFadeSeconds;
        set
        {
            if (!Set(ref _masterFadeSeconds, value)) return;
            OnPropertyChanged(nameof(FadeAllLabel));
            ScheduleSave();
        }
    }

    public string FadeAllLabel => $"SFUMA TUTTO {MasterFadeSeconds:0.#}s";

    private CueViewModel? _nextCue;
    public CueViewModel? NextCue
    {
        get => _nextCue;
        set
        {
            var old = _nextCue;
            if (!Set(ref _nextCue, value)) return;
            if (old != null) old.IsNext = false;
            if (value != null) value.IsNext = !HasCommands; // with commands, GO follows them instead
            OnPropertyChanged(nameof(NextTitle));
            OnPropertyChanged(nameof(NextSummary));
        }
    }

    public string NextTitle => HasCommands
        ? (_nextCommand == null ? "— fine dei comandi —" : $"{_nextCommand.Number}. {_nextCommand.Name}")
        : (_nextCue == null ? "— fine scaletta —" : $"{_nextCue.Number}. {_nextCue.Title}");

    private double _meterL, _meterR;
    public double MeterL { get => _meterL; private set => Set(ref _meterL, value); }
    public double MeterR { get => _meterR; private set => Set(ref _meterR, value); }

    public bool AnyActive => Cues.Any(c => c.IsActive);

    // ---------------- output device ----------------

    /// <summary>True while playing on a device other than the chosen one (it was unavailable).</summary>
    private bool _onFallback;

    /// <summary>
    /// Opens the chosen output; if it cannot be opened (e.g. Bluetooth headphones switched off but still
    /// set as Windows default) tries the Windows default and then every other active output, so the show
    /// never stays silent. Keeps retrying the chosen one when devices change.
    /// </summary>
    private void StartOutput()
    {
        var wanted = _selectedDevice ?? new DeviceItem(null, "Predefinita di Windows");
        var candidates = new List<DeviceItem> { wanted };
        if (wanted.Id != null) candidates.Add(new DeviceItem(null, "Predefinita di Windows"));
        candidates.AddRange(_devices.List().Where(d => d.Id != null && d.Id != wanted.Id));

        string? firstError = null;
        foreach (var c in candidates)
        {
            try
            {
                _engine.Start(c.Id);
                _onFallback = c != wanted;
                OutputText = _onFallback
                    ? $"⚠ Uscita: {c.Name} (ripiego — «{wanted.Name}» non disponibile)"
                    : "Uscita: " + c.Name;
                Status = _onFallback
                    ? $"⚠ «{wanted.Name}» non si apre ({firstError}): sto usando «{c.Name}»"
                    : "Pronto";
                return;
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
            }
        }
        OutputText = "Nessuna uscita audio";
        Status = $"⚠ Nessuna uscita audio disponibile ({firstError}) — riprovo…";
        _recoverTimer.Interval = TimeSpan.FromSeconds(2);
        _recoverTimer.Start();
    }

    private void RecoverOutput()
    {
        _recoverTimer.Interval = TimeSpan.FromMilliseconds(500);
        if (_engine.IsRunning) return;
        StartOutput();
    }

    private void OnDevicesChanged()
    {
        var list = _devices.List();
        _suppressDeviceChange = true;
        var keepId = _selectedDevice?.Id;
        Devices.Clear();
        foreach (var d in list) Devices.Add(d);
        _selectedDevice = Devices.FirstOrDefault(d => d.Id == keepId) ?? Devices[0];
        OnPropertyChanged(nameof(SelectedDevice));
        _suppressDeviceChange = false;

        // following the Windows default: move to the new default device
        if (_selectedDevice.Id == null || !_engine.IsRunning || _onFallback) StartOutput();
    }

    // ---------------- folder ----------------

    public void OpenFolder(string folder)
    {
        CloseFolder();
        Folder = folder;
        _app.LastFolder = folder;
        _app.Save();
        _show = ShowSettings.Load(folder);
        SaveText = File.Exists(Path.Combine(folder, ShowSettings.FileName))
            ? $"✓ impostazioni caricate da {ShowSettings.FileName}"
            : "";
        _masterVolume = _show.MasterVolume;
        _engine.SetMasterVolume((float)(_masterVolume / 100));
        OnPropertyChanged(nameof(MasterVolume));
        _masterFadeSeconds = _show.MasterFadeSeconds;
        OnPropertyChanged(nameof(MasterFadeSeconds));
        OnPropertyChanged(nameof(FadeAllLabel));
        foreach (var b in MasterEqBands)
            b.Gain = _show.MasterEq != null && b.Index < _show.MasterEq.Length ? _show.MasterEq[b.Index] : 0;
        Commands.Clear();
        foreach (var c in _show.Commands) Commands.Add(new CommandViewModel(this, c));
        CommandsChanged();
        NextCommand = Commands.FirstOrDefault();
        foreach (var c in Commands) c.IsLast = false;
        _saveTimer.Stop(); // loading values is not a change

        Rescan();
        NextCue = Cues.FirstOrDefault();

        try
        {
            _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, EnableRaisingEvents = true };
            FileSystemEventHandler h = (_, e) => { if (AudioLoader.IsSupported(e.FullPath)) _ui.BeginInvoke(RestartRescanTimer); };
            _watcher.Created += h;
            _watcher.Deleted += h;
            _watcher.Changed += h;
            _watcher.Renamed += (_, _) => _ui.BeginInvoke(RestartRescanTimer);
        }
        catch { /* network folder without notifications: Aggiorna still works */ }
    }

    private void RestartRescanTimer()
    {
        _rescanTimer.Stop();
        _rescanTimer.Start();
    }

    private void CloseFolder()
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveShow(); }
        _watcher?.Dispose();
        _watcher = null;
        _generation++;
        NextCue = null;
        foreach (var c in Cues)
        {
            var v = c.Detach();
            if (v != null) _engine.RemoveVoice(v);
        }
        Cues.Clear();
        HasCues = false;
        Folder = null;
    }

    /// <summary>Syncs the list with the folder: new files are added, removed ones dropped, replaced ones reloaded.</summary>
    public void Rescan()
    {
        if (_folder == null) return;
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(_folder).Where(AudioLoader.IsSupported).ToList();
        }
        catch (Exception ex)
        {
            Status = $"⚠ Cartella non leggibile: {ex.Message}";
            return;
        }
        files.Sort((a, b) => StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b)));

        var byPath = Cues.ToDictionary(c => c.FilePath, StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

        // removed files (a playing one keeps playing from memory until stopped, then goes)
        foreach (var c in Cues.Where(c => !wanted.Contains(c.FilePath) && !c.IsActive).ToList())
            RemoveCue(c);

        foreach (var f in files)
        {
            if (byPath.TryGetValue(f, out var existing))
            {
                // replaced with a new version and not playing: reload it
                if (!existing.IsActive && existing.Stamp != CueViewModel.FileStamp(f) && existing.IsReady)
                {
                    existing.Stamp = CueViewModel.FileStamp(f);
                    var old = existing.Detach();
                    if (old != null) _engine.RemoveVoice(old);
                    Load(existing);
                }
                else if (existing.Error != null && existing.Stamp != CueViewModel.FileStamp(f))
                {
                    existing.Stamp = CueViewModel.FileStamp(f);
                    existing.Error = null;
                    Load(existing);
                }
                continue;
            }
            _show.Cues.TryGetValue(Path.GetFileName(f), out var saved);
            var cue = new CueViewModel(this, f, saved);
            Cues.Add(cue);
            Load(cue);
        }

        // order like Explorer (1, 2, 10 …) — played files that vanished stay at the end
        var ordered = Cues.OrderBy(c => wanted.Contains(c.FilePath) ? 0 : 1)
                          .ThenBy(c => Path.GetFileName(c.FilePath), Comparer<string>.Create(StrCmpLogicalW))
                          .ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int cur = Cues.IndexOf(ordered[i]);
            if (cur != i) Cues.Move(cur, i);
            ordered[i].Number = i + 1;
        }
        HasCues = Cues.Count > 0;
        if (_nextCue != null && !Cues.Contains(_nextCue)) NextCue = Cues.FirstOrDefault();
        if (_nextCue == null && Cues.Count > 0 && Cues.All(c => !c.IsActive)) NextCue = Cues[0];
        OnPropertyChanged(nameof(NextTitle));
    }

    private void RemoveCue(CueViewModel c)
    {
        if (c == _nextCue) NextCue = Following(c);
        var v = c.Detach();
        if (v != null) _engine.RemoveVoice(v);
        Cues.Remove(c);
    }

    private static readonly SemaphoreSlim LoadGate = new(3);

    private async void Load(CueViewModel cue)
    {
        int gen = _generation;
        WaveStreamResult result;
        await LoadGate.WaitAsync();
        try
        {
            result = await Task.Run(() =>
            {
                try { return new WaveStreamResult(AudioLoader.Open(cue.FilePath), null); }
                catch (Exception ex) { return new WaveStreamResult(null, ex); }
            });
        }
        finally { LoadGate.Release(); }

        if (gen != _generation || !Cues.Contains(cue) || cue.IsReady)
        {
            result.Reader?.Dispose();
            return;
        }
        if (result.Reader == null)
        {
            cue.Error = "formato non leggibile" + (result.Error is FileNotFoundException ? " (file sparito)" : "");
            return;
        }
        cue.Attach(_engine.CreateVoice(result.Reader));
        if (Cues.All(c => c.IsReady || c.Error != null))
        {
            int bad = Cues.Count(c => c.Error != null);
            Status = bad == 0 ? $"Pronto — {Cues.Count} tracce caricate" : $"⚠ {bad} file non leggibili (in rosso)";
        }
        else
        {
            Status = $"Caricamento… {Cues.Count(c => c.IsReady)}/{Cues.Count}";
        }
    }

    private readonly record struct WaveStreamResult(NAudio.Wave.WaveStream? Reader, Exception? Error);

    // ---------------- transport ----------------

    /// <summary>Space / GO: runs the next command (or, with no commands, plays the next track).</summary>
    public void Go()
    {
        // ignore an accidental double tap
        if ((DateTime.UtcNow - _lastGo).TotalMilliseconds < 350) return;
        _lastGo = DateTime.UtcNow;

        if (HasCommands)
        {
            if (_nextCommand != null) Run(_nextCommand);
            return;
        }
        var cue = _nextCue;
        if (cue == null) return;
        if (cue.IsReady && cue.State != PlayState.Playing) cue.Voice!.Play();
        cue.Refresh();
        NextCue = Following(cue);
    }

    /// <summary>A cue was started by hand: if it was the next one, GO moves on.</summary>
    public void Touch(CueViewModel cue)
    {
        if (cue == _nextCue) NextCue = Following(cue);
    }

    public void SelectNext(CueViewModel cue) => NextCue = cue;

    public void MoveNext(int delta)
    {
        if (HasCommands)
        {
            int k = _nextCommand == null ? (delta > 0 ? -1 : Commands.Count) : Commands.IndexOf(_nextCommand);
            NextCommand = Commands[Math.Clamp(k + delta, 0, Commands.Count - 1)];
            return;
        }
        if (Cues.Count == 0) return;
        int i = _nextCue == null ? (delta > 0 ? -1 : Cues.Count) : Cues.IndexOf(_nextCue);
        NextCue = Cues[Math.Clamp(i + delta, 0, Cues.Count - 1)];
    }

    private CueViewModel? Following(CueViewModel cue)
    {
        int i = Cues.IndexOf(cue);
        return i >= 0 && i + 1 < Cues.Count ? Cues[i + 1] : null;
    }

    public void StopAll()
    {
        foreach (var c in Cues) { c.Voice?.Stop(); c.Refresh(); }
    }

    public void FadeAll()
    {
        foreach (var c in Cues.Where(c => c.State == PlayState.Playing)) { c.Voice?.FadeOut(MasterFadeSeconds); c.Refresh(); }
    }

    // ---------------- ticking, saving ----------------

    private void OnTick()
    {
        foreach (var c in Cues) c.Refresh();
        var (l, r) = _engine.TakePeak();
        MeterL = ToMeter(l);
        MeterR = ToMeter(r);
    }

    private static double ToMeter(float peak)
    {
        if (peak <= 0.001f) return 0;
        double db = 20 * Math.Log10(peak);
        return Math.Clamp((db + 60) / 60, 0, 1);
    }

    public void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveShow()
    {
        if (_folder == null) return;
        _show.MasterVolume = _masterVolume;
        _show.MasterFadeSeconds = _masterFadeSeconds;
        _show.MasterEq = MasterEqActive ? MasterEqBands.Select(b => b.Gain).ToArray() : null;
        _show.Commands = Commands.Select(c => c.Model).ToList();
        foreach (var c in Cues) _show.Cues[c.FileName] = c.ToSettings();
        if (_show.Save(_folder))
            SaveText = $"✓ impostazioni salvate in {ShowSettings.FileName} · {DateTime.Now:HH:mm:ss}";
        else
        {
            SaveText = "⚠ non salvato";
            Status = "⚠ Impossibile salvare le impostazioni nella cartella (sola lettura?)";
        }
    }

    // ---------------- misc ----------------

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private static void PreventSleep(bool on)
    {
        const uint continuous = 0x80000000, system = 0x1, display = 0x2;
        try { SetThreadExecutionState(on ? continuous | system | display : continuous); } catch { }
    }

    public void Dispose()
    {
        _tick.Stop();
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveShow(); }
        _watcher?.Dispose();
        PreventSleep(false);
        _engine.Dispose();
        _devices.Dispose();
    }
}
