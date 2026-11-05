using System.Runtime.CompilerServices;

namespace CuePlayer.ViewModels;

/// <summary>Switches of the "Funzioni" page. Each optional feature is hidden/inactive while switched off.</summary>
public sealed class FeaturesViewModel : ObservableObject
{
    private readonly FeatureFlags _f;
    private readonly Action _save;

    public FeaturesViewModel(FeatureFlags flags, Action save)
    {
        _f = flags;
        _save = save;
    }

    private bool Get(Func<FeatureFlags, bool> get) => get(_f);

    private void Put(bool current, bool value, Action<FeatureFlags> set, [CallerMemberName] string? name = null)
    {
        if (current == value) return;
        set(_f);
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(AnyShowTools));
        _save();
    }

    public bool ShowMode { get => _f.ShowMode; set => Put(_f.ShowMode, value, f => f.ShowMode = value); }
    public bool Notes { get => _f.Notes; set => Put(_f.Notes, value, f => f.Notes = value); }
    public bool Trim { get => _f.Trim; set => Put(_f.Trim, value, f => f.Trim = value); }
    public bool EndWarning { get => _f.EndWarning; set => Put(_f.EndWarning, value, f => f.EndWarning = value); }
    public bool TrackEq { get => _f.TrackEq; set => Put(_f.TrackEq, value, f => f.TrackEq = value); }
    public bool MasterEq { get => _f.MasterEq; set => Put(_f.MasterEq, value, f => f.MasterEq = value); }
    public bool Preview { get => _f.Preview; set => Put(_f.Preview, value, f => f.Preview = value); }
    public bool Waveform { get => _f.Waveform; set => Put(_f.Waveform, value, f => f.Waveform = value); }
    public bool Chain { get => _f.Chain; set => Put(_f.Chain, value, f => f.Chain = value); }
    public bool Undo { get => _f.Undo; set => Put(_f.Undo, value, f => f.Undo = value); }
    public bool Normalize { get => _f.Normalize; set => Put(_f.Normalize, value, f => f.Normalize = value); }
    public bool Limiter { get => _f.Limiter; set => Put(_f.Limiter, value, f => f.Limiter = value); }
    public bool Acts { get => _f.Acts; set => Put(_f.Acts, value, f => f.Acts = value); }
    public bool PrintList { get => _f.PrintList; set => Put(_f.PrintList, value, f => f.PrintList = value); }
    public bool Backup { get => _f.Backup; set => Put(_f.Backup, value, f => f.Backup = value); }

    /// <summary>Show-menu has at least one entry.</summary>
    public bool AnyShowTools => _f.PrintList || _f.Backup;
}
