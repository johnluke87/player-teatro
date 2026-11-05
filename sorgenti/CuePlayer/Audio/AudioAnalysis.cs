using NAudio.Wave;

namespace CuePlayer.Audio;

/// <summary>
/// One pass over a whole file (in background, after loading): waveform overview and loudness,
/// used for the waveform display and for "volumi uniformati".
/// </summary>
public sealed class AudioAnalysis
{
    public const int Buckets = 600;

    /// <summary>Peak per bucket, 0..1.</summary>
    public float[] Peaks { get; init; } = Array.Empty<float>();
    /// <summary>Average loudness (RMS of the louder half of the file, dBFS) — close to how loud it "feels".</summary>
    public double LoudnessDb { get; init; }
    public double PeakDb { get; init; }

    /// <summary>Target loudness for normalisation, dBFS RMS.</summary>
    public const double TargetDb = -18;

    /// <summary>Gain (dB) that brings the track to <see cref="TargetDb"/>, limited so it never clips and never boosts more than 12 dB.</summary>
    public double NormalizationDb
    {
        get
        {
            if (double.IsInfinity(LoudnessDb) || LoudnessDb < -70) return 0;
            double g = TargetDb - LoudnessDb;
            g = Math.Min(g, -1 - PeakDb); // keep peaks under -1 dBFS
            return Math.Clamp(g, -18, 12);
        }
    }

    public static AudioAnalysis Analyze(string path)
    {
        using var reader = AudioLoader.Open(path);
        var sp = reader.ToSampleProvider();
        int ch = sp.WaveFormat.Channels;
        long totalFrames = Math.Max(1, (long)(reader.TotalTime.TotalSeconds * sp.WaveFormat.SampleRate));
        long framesPerBucket = Math.Max(1, totalFrames / Buckets);

        var peaks = new float[Buckets];
        // loudness: RMS per 400 ms block, then average of the louder half (ignores silences and fades)
        int blockFrames = Math.Max(1, sp.WaveFormat.SampleRate * 4 / 10);
        var blocks = new List<double>();
        double blockSum = 0; int blockN = 0;
        float peak = 0;

        var buf = new float[ch * 4096];
        long frame = 0;
        int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i + ch <= n; i += ch)
            {
                float m = 0; double sq = 0;
                for (int c = 0; c < ch; c++)
                {
                    float v = buf[i + c];
                    float a = v < 0 ? -v : v;
                    if (a > m) m = a;
                    sq += v * v;
                }
                sq /= ch;
                int b = (int)Math.Min(Buckets - 1, frame / framesPerBucket);
                if (m > peaks[b]) peaks[b] = m;
                if (m > peak) peak = m;
                blockSum += sq; blockN++;
                if (blockN == blockFrames) { blocks.Add(blockSum / blockN); blockSum = 0; blockN = 0; }
                frame++;
            }
        }
        if (blockN > blockFrames / 4) blocks.Add(blockSum / blockN);

        double loud = double.NegativeInfinity;
        if (blocks.Count > 0)
        {
            blocks.Sort();
            var top = blocks.Skip(blocks.Count / 2).ToList();
            double mean = top.Average();
            loud = mean > 0 ? 10 * Math.Log10(mean) : double.NegativeInfinity;
        }
        return new AudioAnalysis
        {
            Peaks = peaks,
            LoudnessDb = loud,
            PeakDb = peak > 0 ? 20 * Math.Log10(peak) : -120,
        };
    }
}
