namespace CuePlayer.Audio;

public enum BandKind { LowShelf, Peak, HighShelf }

public sealed record EqBandSpec(string Label, double Frequency, BandKind Kind, double Q);

/// <summary>
/// Stereo parametric EQ (RBJ biquads, double precision). Bypassed entirely while all bands are at 0 dB,
/// so a flat EQ leaves the audio bit-identical. Callers hold the engine lock.
/// </summary>
public sealed class Equalizer
{
    public const double MaxDb = 12;

    public static readonly EqBandSpec[] TrackBands =
    {
        new("Bassi", 150, BandKind.LowShelf, 0.707),
        new("Medi", 1000, BandKind.Peak, 0.8),
        new("Alti", 6000, BandKind.HighShelf, 0.707),
    };

    public static readonly EqBandSpec[] MasterBands =
    {
        new("80 Hz", 80, BandKind.LowShelf, 0.707),
        new("250 Hz", 250, BandKind.Peak, 1.0),
        new("1 kHz", 1000, BandKind.Peak, 1.0),
        new("3,5 kHz", 3500, BandKind.Peak, 1.0),
        new("10 kHz", 10000, BandKind.HighShelf, 0.707),
    };

    private readonly EqBandSpec[] _spec;
    private readonly double[] _db;
    private readonly double[] _b0, _b1, _b2, _a1, _a2;
    // per band, per channel: x1, x2, y1, y2
    private readonly double[,] _z;
    private int _rate;
    private bool _active;

    public Equalizer(EqBandSpec[] spec, int sampleRate)
    {
        _spec = spec;
        int n = spec.Length;
        _db = new double[n];
        _b0 = new double[n]; _b1 = new double[n]; _b2 = new double[n]; _a1 = new double[n]; _a2 = new double[n];
        _z = new double[n, 8];
        SetSampleRate(sampleRate);
    }

    public int BandCount => _spec.Length;
    public bool IsActive => _active;

    public void SetSampleRate(int rate)
    {
        _rate = rate;
        for (int b = 0; b < _spec.Length; b++) Compute(b);
        Array.Clear(_z);
    }

    public void SetGain(int band, double db)
    {
        if (band < 0 || band >= _db.Length) return;
        _db[band] = Math.Clamp(db, -MaxDb, MaxDb);
        Compute(band);
        bool active = _db.Any(d => Math.Abs(d) > 0.05);
        if (active && !_active) Array.Clear(_z); // no stale history when switching in
        _active = active;
    }

    public double GetGain(int band) => _db[band];

    private void Compute(int b)
    {
        var s = _spec[b];
        double f = Math.Min(s.Frequency, _rate * 0.45);
        double A = Math.Pow(10, _db[b] / 40);
        double w0 = 2 * Math.PI * f / _rate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / (2 * s.Q);
        double sqA2a = 2 * Math.Sqrt(A) * alpha;
        double b0, b1, b2, a0, a1, a2;
        switch (s.Kind)
        {
            case BandKind.LowShelf:
                b0 = A * ((A + 1) - (A - 1) * cos + sqA2a);
                b1 = 2 * A * ((A - 1) - (A + 1) * cos);
                b2 = A * ((A + 1) - (A - 1) * cos - sqA2a);
                a0 = (A + 1) + (A - 1) * cos + sqA2a;
                a1 = -2 * ((A - 1) + (A + 1) * cos);
                a2 = (A + 1) + (A - 1) * cos - sqA2a;
                break;
            case BandKind.HighShelf:
                b0 = A * ((A + 1) + (A - 1) * cos + sqA2a);
                b1 = -2 * A * ((A - 1) + (A + 1) * cos);
                b2 = A * ((A + 1) + (A - 1) * cos - sqA2a);
                a0 = (A + 1) - (A - 1) * cos + sqA2a;
                a1 = 2 * ((A - 1) - (A + 1) * cos);
                a2 = (A + 1) - (A - 1) * cos - sqA2a;
                break;
            default:
                b0 = 1 + alpha * A;
                b1 = -2 * cos;
                b2 = 1 - alpha * A;
                a0 = 1 + alpha / A;
                a1 = -2 * cos;
                a2 = 1 - alpha / A;
                break;
        }
        _b0[b] = b0 / a0; _b1[b] = b1 / a0; _b2[b] = b2 / a0; _a1[b] = a1 / a0; _a2[b] = a2 / a0;
    }

    /// <summary>Filters interleaved stereo in place.</summary>
    public void Process(float[] buffer, int offset, int frames)
    {
        if (!_active) return;
        for (int b = 0; b < _spec.Length; b++)
        {
            if (Math.Abs(_db[b]) <= 0.05) continue;
            double b0 = _b0[b], b1 = _b1[b], b2 = _b2[b], a1 = _a1[b], a2 = _a2[b];
            for (int ch = 0; ch < 2; ch++)
            {
                int z = ch * 4;
                double x1 = _z[b, z], x2 = _z[b, z + 1], y1 = _z[b, z + 2], y2 = _z[b, z + 3];
                for (int f = 0, i = offset + ch; f < frames; f++, i += 2)
                {
                    double x = buffer[i];
                    double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                    if (y is < 1e-25 and > -1e-25) y = 0; // avoid slow denormals in silence
                    x2 = x1; x1 = x; y2 = y1; y1 = y;
                    buffer[i] = (float)y;
                }
                _z[b, z] = x1; _z[b, z + 1] = x2; _z[b, z + 2] = y1; _z[b, z + 3] = y2;
            }
        }
    }
}
