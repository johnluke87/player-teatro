using CuePlayer.Audio;

namespace CuePlayer.ViewModels;

/// <summary>One EQ slider (dB). Pushes changes to the audio side through the callback.</summary>
public sealed class EqBandViewModel : ObservableObject
{
    private readonly Action<int, double> _apply;

    public EqBandViewModel(int index, EqBandSpec spec, Action<int, double> apply)
    {
        Index = index;
        Label = spec.Label;
        _apply = apply;
    }

    public int Index { get; }
    public string Label { get; }
    public double Min => -Equalizer.MaxDb;
    public double Max => Equalizer.MaxDb;

    private double _gain;
    public double Gain
    {
        get => _gain;
        set
        {
            value = Math.Round(Math.Clamp(value, -Equalizer.MaxDb, Equalizer.MaxDb) * 2) / 2; // 0.5 dB steps
            if (!Set(ref _gain, value)) return;
            OnPropertyChanged(nameof(GainText));
            _apply(Index, value);
        }
    }

    public string GainText => _gain == 0 ? "0 dB" : $"{_gain:+0.#;-0.#} dB";

    public static List<EqBandViewModel> Create(EqBandSpec[] specs, double[]? saved, Action<int, double> apply)
    {
        var list = new List<EqBandViewModel>();
        for (int i = 0; i < specs.Length; i++)
        {
            var b = new EqBandViewModel(i, specs[i], apply);
            if (saved != null && i < saved.Length) b.Gain = saved[i];
            list.Add(b);
        }
        return list;
    }
}
