using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CuePlayer.Audio;
using NAudio.Wave;

namespace CuePlayer.ViewModels;

/// <summary>Optional features switched on in the "Funzioni" page.</summary>
public sealed partial class MainViewModel
{
    // =====================================================================
    // wiring (called from the constructor / Initialize / Dispose)
    // =====================================================================

    private DispatcherTimer _chainTimer = null!;

    private void InitExtras()
    {
        _chainTimer = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromMilliseconds(100) };
        _chainTimer.Tick += (_, _) => ChainTick();
        UndoCommand = new RelayCommand(Undo);
        Features.PropertyChanged += (_, e) => OnFeatureChanged(e.PropertyName);
        _engine.Limiter = Features.Limiter;
    }

    private void OnFeatureChanged(string? name)
    {
        switch (name)
        {
            case nameof(FeaturesViewModel.Limiter): _engine.Limiter = Features.Limiter; break;
            case nameof(FeaturesViewModel.Normalize): foreach (var c in Cues) c.ApplyNormalization(); break;
            case nameof(FeaturesViewModel.Acts): UpdateGrouping(); break;
            case nameof(FeaturesViewModel.Chain): if (!Features.Chain) CancelChain(); break;
            case nameof(FeaturesViewModel.Undo): _undo.Clear(); OnPropertyChanged(nameof(CanUndo)); break;
            case nameof(FeaturesViewModel.Preview): if (!Features.Preview) StopPreview(); break;
        }
        OnPropertyChanged(nameof(Features));
    }

    private void DisposeExtras()
    {
        _chainTimer?.Stop();
        StopPreview();
        _previewEngine?.Dispose();
    }

    /// <summary>Called by the tick timer.</summary>
    private void TickExtras()
    {
        if (_previewVoice != null && _previewVoice.GetStatus().State == PlayState.Stopped) StopPreview();
    }

    // =====================================================================
    // waveform + loudness analysis (background, after loading)
    // =====================================================================

    private void Analyze(CueViewModel cue)
    {
        int gen = _generation;
        Task.Run(() =>
        {
            try { return AudioAnalysis.Analyze(cue.FilePath); }
            catch { return null; }
        }).ContinueWith(t =>
        {
            if (t.Result != null && gen == _generation) cue.SetAnalysis(t.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // =====================================================================
    // headphone preview: a second, independent output
    // =====================================================================

    private AudioEngine? _previewEngine;
    private Voice? _previewVoice;
    private CueViewModel? _previewCue;

    private DeviceItem? _previewDevice;
    /// <summary>Where the pre-listen goes (the technician's headphones).</summary>
    public DeviceItem? PreviewDevice
    {
        get => _previewDevice;
        set
        {
            if (value == null || !Set(ref _previewDevice, value) || _suppressDeviceChange) return;
            _app.PreviewDeviceId = value.Id;
            _app.Save();
            StopPreview();
            _previewEngine?.Stop();
        }
    }

    private void SelectPreviewDevice()
    {
        var keep = _app.PreviewDeviceId;
        _previewDevice = Devices.FirstOrDefault(d => keep != null && d.Id == keep)
                         ?? Devices.FirstOrDefault(d => d.Id != null && d.Id != _selectedDevice?.Id)
                         ?? Devices.FirstOrDefault();
        OnPropertyChanged(nameof(PreviewDevice));
    }

    public async void TogglePreview(CueViewModel cue)
    {
        if (_previewCue == cue) { StopPreview(); return; }
        StopPreview();
        _previewEngine ??= new AudioEngine();
        if (!_previewEngine.IsRunning)
        {
            try { _previewEngine.Start(_previewDevice?.Id); }
            catch (Exception ex)
            {
                Status = $"⚠ Cuffia per il pre-ascolto non disponibile ({ex.Message})";
                return;
            }
        }
        _previewCue = cue;
        cue.IsPreviewing = true;
        WaveStream reader;
        try { reader = await Task.Run(() => AudioLoader.Open(cue.FilePath)); }
        catch { StopPreview(); return; }
        if (_previewCue != cue) { reader.Dispose(); return; }
        _previewVoice = _previewEngine.CreateVoice(reader);
        _previewVoice.SetTrim(TimeSpan.FromSeconds(cue.StartAt), cue.EndAt is { } e ? TimeSpan.FromSeconds(e) : null, cue.EndFade);
        _previewVoice.Play();
        Status = $"🎧 Pre-ascolto in cuffia: {cue.Title} ({_previewDevice?.Name})";
    }

    public void StopPreview()
    {
        if (_previewCue != null) _previewCue.IsPreviewing = false;
        _previewCue = null;
        if (_previewVoice != null && _previewEngine != null) _previewEngine.RemoveVoice(_previewVoice);
        _previewVoice = null;
    }

    // =====================================================================
    // chained commands: "after N s run the next one by itself"
    // =====================================================================

    private CommandViewModel? _chainTarget;
    private DateTime _chainAt;

    private string _chainText = "";
    /// <summary>"⏱ «Entra MusicaB» tra 3.2 s" while a chain countdown runs.</summary>
    public string ChainText { get => _chainText; private set { if (Set(ref _chainText, value)) OnPropertyChanged(nameof(HasChain)); } }
    public bool HasChain => _chainText.Length > 0;

    private void StartChain(CommandViewModel cmd)
    {
        CancelChain();
        if (!Features.Chain || !cmd.Model.AutoNext) return;
        int i = Commands.IndexOf(cmd);
        if (i < 0 || i + 1 >= Commands.Count) return;
        _chainTarget = Commands[i + 1];
        _chainAt = DateTime.Now.AddSeconds(Math.Max(0, cmd.Model.AutoNextSeconds));
        ChainTick();
        _chainTimer.Start();
    }

    private void ChainTick()
    {
        if (_chainTarget == null) { CancelChain(); return; }
        double left = (_chainAt - DateTime.Now).TotalSeconds;
        if (left <= 0)
        {
            var t = _chainTarget;
            CancelChain();
            if (Commands.Contains(t)) Run(t);
            return;
        }
        ChainText = $"⏱ «{_chainTarget.Name}» parte da solo tra {left:0.0} s";
    }

    public void CancelChain()
    {
        _chainTimer?.Stop();
        _chainTarget = null;
        ChainText = "";
    }

    // =====================================================================
    // undo: back to the situation before the last command
    // =====================================================================

    private sealed record TrackState(CueViewModel Cue, PlayState State, TimeSpan Position, float Level);
    private sealed record UndoState(CommandViewModel Cmd, CommandViewModel? PrevLast, List<TrackState> Tracks, DateTime At);
    private readonly Stack<UndoState> _undo = new();

    public ICommand UndoCommand { get; private set; } = null!;
    public bool CanUndo => _undo.Count > 0;
    public string UndoLabel => _undo.Count > 0 ? $"Annulla «{_undo.Peek().Cmd.Name}»" : "Niente da annullare";

    private void PushUndo(CommandViewModel cmd)
    {
        if (!Features.Undo) return;
        var tracks = Cues.Where(c => c.Voice != null)
                         .Select(c => { var s = c.Voice!.GetStatus(); return new TrackState(c, s.State, s.Position, s.Level); })
                         .ToList();
        _undo.Push(new UndoState(cmd, _lastCommand, tracks, DateTime.Now));
        while (_undo.Count > 30) { var keep = _undo.Take(30).Reverse().ToList(); _undo.Clear(); foreach (var k in keep) _undo.Push(k); }
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoLabel));
    }

    /// <summary>Ctrl+Z / ↶: every track goes back (smoothly, in 0.5 s) to how it was before the last command.</summary>
    public void Undo()
    {
        if (_undo.Count == 0) return;
        CancelChain();
        var u = _undo.Pop();
        var elapsed = DateTime.Now - u.At;
        foreach (var t in u.Tracks)
        {
            var v = t.Cue.Voice;
            if (v == null) continue;
            var now = v.GetStatus();
            switch (t.State)
            {
                case PlayState.Stopped:
                    if (now.State == PlayState.Playing) v.FadeOut(0.5, crossfade: true);
                    else if (now.State == PlayState.Paused) v.Stop();
                    break;
                case PlayState.Paused:
                    if (now.State == PlayState.Playing) v.Pause();
                    v.Seek(t.Position);
                    break;
                case PlayState.Playing:
                    if (now.State != PlayState.Playing)
                    {
                        // it was stopped/paused by the command: pick up where it would be now
                        var pos = t.Position + elapsed;
                        if (pos < now.End) v.Seek(pos);
                    }
                    v.FadeTo(Math.Max(0.02, t.Level), 0.5);
                    break;
            }
            t.Cue.Refresh();
        }
        foreach (var c in Commands) c.IsLast = c == u.PrevLast;
        LastCommand = u.PrevLast;
        if (Commands.Contains(u.Cmd)) NextCommand = u.Cmd;
        Status = $"↶ Annullato «{u.Cmd.Name}»";
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoLabel));
    }

    // =====================================================================
    // acts / scenes: commands grouped under headers
    // =====================================================================

    /// <summary>The commands as shown in the panel (grouped by act when that feature is on).</summary>
    public ICollectionView CommandsView => CollectionViewSource.GetDefaultView(Commands);

    /// <summary>Act names already used, for the editor's suggestion list.</summary>
    public List<string> KnownGroups => Commands.Select(c => c.Model.Group?.Trim() ?? "")
                                               .Where(g => g.Length > 0).Distinct().ToList();

    private void UpdateGrouping()
    {
        var view = CommandsView;
        bool group = Features.Acts && Commands.Any(c => !string.IsNullOrWhiteSpace(c.Model.Group));
        using (view.DeferRefresh())
        {
            view.GroupDescriptions.Clear();
            if (group) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CommandViewModel.Group)));
        }
    }

    // =====================================================================
    // printable cue list
    // =====================================================================

    /// <summary>Writes the cue list as a web page and opens it (the browser shows the print dialog).</summary>
    public void PrintCueList()
    {
        string E(string s) => WebUtility.HtmlEncode(s);
        string show = _folder != null ? Path.GetFileName(_folder.TrimEnd('\\')) : "Spettacolo";
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=it><head><meta charset=utf-8><title>Scaletta · ").Append(E(show)).Append("</title><style>")
          .Append("body{font-family:Segoe UI,Arial,sans-serif;margin:18mm;color:#1b1f24;font-size:11pt}h1{margin:0;font-size:22pt}")
          .Append(".sub{color:#5b6470;margin:2mm 0 8mm}table{width:100%;border-collapse:collapse}th,td{border-bottom:1px solid #d9dde3;padding:2.5mm 2mm;text-align:left;vertical-align:top}")
          .Append("th{font-size:9pt;color:#5b6470;text-transform:uppercase}.n{font-weight:700;font-size:13pt;width:10mm}.name{font-weight:600;font-size:12pt}")
          .Append(".note{font-style:italic;color:#8a6200}.act td{background:#f3f5f8;font-weight:700;font-size:12pt;border-bottom:2px solid #1b1f24}")
          .Append(".acts{font-family:Consolas,monospace;font-size:9.5pt;white-space:pre-line}.auto{color:#2f6fd6;font-size:9pt}.dot{display:inline-block;width:3mm;height:3mm;border-radius:50%;margin-right:2mm}")
          .Append("@media print{body{margin:10mm}}</style><script>window.onload=()=>setTimeout(()=>window.print(),300)</script></head><body>")
          .Append("<h1>Scaletta · ").Append(E(show)).Append("</h1><div class=sub>RTC Stage · ").Append(DateTime.Now.ToString("dd/MM/yyyy HH:mm"))
          .Append(" · ").Append(Commands.Count).Append(" comandi</div><table><tr><th>#</th><th>Comando</th><th>Quando</th><th>Cosa fa</th></tr>");
        string? act = null;
        bool acts = Features.Acts && Commands.Any(c => !string.IsNullOrWhiteSpace(c.Model.Group));
        foreach (var c in Commands)
        {
            if (acts && c.Group != act) { act = c.Group; sb.Append("<tr class=act><td colspan=4>").Append(E(act)).Append("</td></tr>"); }
            sb.Append("<tr><td class=n>").Append(c.Number).Append("</td><td><span class=dot style='background:").Append(E(c.Model.Color)).Append("'></span><span class=name>")
              .Append(E(c.Name)).Append("</span>");
            if (c.AutoNext && Features.Chain) sb.Append("<div class=auto>").Append(E(c.AutoNextText)).Append("</div>");
            sb.Append("</td><td class=note>").Append(Features.Notes ? E(c.Note) : "").Append("</td><td class=acts>").Append(E(c.Summary)).Append("</td></tr>");
        }
        sb.Append("</table></body></html>");
        var path = Path.Combine(Path.GetTempPath(), $"RTC Stage - scaletta {show}.html");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        Status = "🖨 Scaletta aperta nel browser: stampala o salvala in PDF";
    }

    // =====================================================================
    // backup / restore of the whole show (music + settings) as one zip
    // =====================================================================

    public string? ShowName => _folder != null ? Path.GetFileName(_folder.TrimEnd('\\')) : null;

    public void BackupTo(string zipPath)
    {
        if (_folder == null) return;
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveShow(); }
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var f in Directory.EnumerateFiles(_folder))
            {
                var name = Path.GetFileName(f);
                if (!AudioLoader.IsSupported(f) && !name.Equals(ShowSettings.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                // audio is already compressed: store it, don't waste time compressing again
                zip.CreateEntryFromFile(f, name, AudioLoader.IsSupported(f) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            }
        }
        Status = $"💾 Backup salvato: {zipPath}";
    }

    public void RestoreFrom(string zipPath, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);
        ZipFile.ExtractToDirectory(zipPath, targetFolder, overwriteFiles: true);
        OpenFolder(targetFolder);
        Status = $"💾 Spettacolo ripristinato in {targetFolder}";
    }
}
