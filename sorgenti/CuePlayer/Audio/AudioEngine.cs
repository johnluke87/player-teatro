using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CuePlayer.Audio;

/// <summary>
/// Always-running mixer: one WASAPI output that never stops, every cue is a
/// <see cref="Voice"/> summed here. Starting a cue only flips a flag, so the
/// sound starts within one output buffer (~60 ms) of the click.
/// </summary>
public sealed class AudioEngine : ISampleProvider, IDisposable
{
    internal readonly object Sync = new();
    private readonly List<Voice> _voices = new();
    private WasapiOut? _out;
    private float _masterTarget = 1f, _masterCurrent = 1f;
    private float _peakL, _peakR;
    private readonly Equalizer _masterEq = new(Equalizer.MasterBands, 48000);

    // ---- output limiter ----
    private const float LimitCeiling = 0.891f; // -1 dBFS
    private bool _limiter;
    private float _limGain = 1f;
    private float _limRelease => 1f / (0.15f * SampleRate);

    /// <summary>When on, the sum never goes above -1 dBFS (no hard clipping when several tracks add up).</summary>
    public bool Limiter
    {
        get { lock (Sync) return _limiter; }
        set { lock (Sync) { _limiter = value; _limGain = 1f; } }
    }

    /// <summary>Master EQ band gain in dB (bands: <see cref="Equalizer.MasterBands"/>).</summary>
    public void SetMasterEq(int band, double db)
    {
        lock (Sync) _masterEq.SetGain(band, db);
    }

    public const int LatencyMs = 60;

    public WaveFormat WaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public int SampleRate => WaveFormat.SampleRate;

    /// <summary>True while the output device is pulling audio. Tests set it without a device.</summary>
    public bool IsRunning { get; internal set; }

    /// <summary>Raised (on the UI thread) when the output stops unexpectedly, e.g. USB card unplugged.</summary>
    public event EventHandler<Exception?>? OutputFailed;

    public Voice CreateVoice(WaveStream reader)
    {
        var v = new Voice(this, reader);
        lock (Sync) _voices.Add(v);
        return v;
    }

    public void RemoveVoice(Voice v)
    {
        lock (Sync) _voices.Remove(v);
        v.Dispose();
    }

    /// <summary>Master volume as slider fraction 0..1 (perceptual curve applied).</summary>
    public void SetMasterVolume(float fraction)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);
        lock (Sync) _masterTarget = fraction * fraction;
    }

    /// <summary>Opens the given WASAPI render device (null = Windows default) and starts pulling audio.</summary>
    public void Start(string? deviceId)
    {
        Stop();
        using var enumerator = new MMDeviceEnumerator();
        var device = deviceId == null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(deviceId);

        int rate = 48000;
        try { rate = device.AudioClient.MixFormat.SampleRate; } catch { /* keep 48k */ }

        lock (Sync)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
            _masterEq.SetSampleRate(rate);
            foreach (var v in _voices) v.RebuildChain();
        }

        var output = new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMs);
        output.PlaybackStopped += OnPlaybackStopped;
        try
        {
            output.Init(this);
            output.Play();
        }
        catch
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            output.Dispose();
            throw;
        }
        _out = output;
        IsRunning = true;
    }

    public void Stop()
    {
        var o = _out;
        _out = null;
        IsRunning = false;
        if (o == null) return;
        o.PlaybackStopped -= OnPlaybackStopped;
        try { o.Stop(); } catch { }
        try { o.Dispose(); } catch { }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (!ReferenceEquals(sender, _out)) return;
        IsRunning = false;
        OutputFailed?.Invoke(this, e.Exception);
    }

    /// <summary>Peak levels since last call (0..1+), for the meter.</summary>
    public (float L, float R) TakePeak()
    {
        lock (Sync)
        {
            var p = (_peakL, _peakR);
            _peakL = _peakR = 0;
            return p;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // NOT Array.Clear: WasapiOut hands us a byte[] disguised as float[] (NAudio WaveBuffer),
        // so Array.Clear would only zero a quarter of it and old audio would echo back in.
        for (int i = offset, end = offset + count; i < end; i++) buffer[i] = 0f;
        lock (Sync)
        {
            for (int i = 0; i < _voices.Count; i++)
            {
                try { _voices[i].MixInto(buffer, offset, count); }
                catch { _voices[i].Fail(); } // a broken file must never kill the output
            }

            int frames = count / 2;
            _masterEq.Process(buffer, offset, frames);
            float g0 = _masterCurrent, g1 = _masterTarget;
            float dg = frames > 0 ? (g1 - g0) / frames : 0f;
            float g = g0, pl = _peakL, pr = _peakR;
            bool limit = _limiter;
            float lg = _limGain;
            for (int f = 0; f < frames; f++)
            {
                g += dg;
                int i = offset + 2 * f;
                float l = buffer[i] * g;
                float r = buffer[i + 1] * g;
                if (limit)
                {
                    // peak limiter: instant attack, ~150 ms release, ceiling -1 dBFS
                    float env = MathF.Max(MathF.Abs(l), MathF.Abs(r)) * lg;
                    if (env > LimitCeiling) lg = LimitCeiling / MathF.Max(MathF.Abs(l), MathF.Abs(r));
                    else lg += (1f - lg) * _limRelease;
                    l *= lg;
                    r *= lg;
                }
                l = Math.Clamp(l, -1f, 1f);
                r = Math.Clamp(r, -1f, 1f);
                buffer[i] = l;
                buffer[i + 1] = r;
                if (l < 0) l = -l;
                if (r < 0) r = -r;
                if (l > pl) pl = l;
                if (r > pr) pr = r;
            }
            _masterCurrent = g1;
            _limGain = lg;
            _peakL = pl;
            _peakR = pr;
        }
        return count;
    }

    public void Dispose()
    {
        Stop();
        lock (Sync)
        {
            foreach (var v in _voices) v.Dispose();
            _voices.Clear();
        }
    }
}
