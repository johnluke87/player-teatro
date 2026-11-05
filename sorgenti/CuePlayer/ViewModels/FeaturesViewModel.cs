namespace CuePlayer.ViewModels;

/// <summary>Checkboxes of the "Funzioni" page. Each optional feature is hidden while switched off.</summary>
public sealed class FeaturesViewModel : ObservableObject
{
    private readonly FeatureFlags _f;
    private readonly Action _save;

    public FeaturesViewModel(FeatureFlags flags, Action save)
    {
        _f = flags;
        _save = save;
    }

    private void Changed(string name)
    {
        OnPropertyChanged(name);
        _save();
    }

    public bool ShowMode { get => _f.ShowMode; set { if (_f.ShowMode != value) { _f.ShowMode = value; Changed(nameof(ShowMode)); } } }
    public bool Notes { get => _f.Notes; set { if (_f.Notes != value) { _f.Notes = value; Changed(nameof(Notes)); } } }
    public bool Trim { get => _f.Trim; set { if (_f.Trim != value) { _f.Trim = value; Changed(nameof(Trim)); } } }
    public bool EndWarning { get => _f.EndWarning; set { if (_f.EndWarning != value) { _f.EndWarning = value; Changed(nameof(EndWarning)); } } }
    public bool TrackEq { get => _f.TrackEq; set { if (_f.TrackEq != value) { _f.TrackEq = value; Changed(nameof(TrackEq)); } } }
    public bool MasterEq { get => _f.MasterEq; set { if (_f.MasterEq != value) { _f.MasterEq = value; Changed(nameof(MasterEq)); } } }
}
