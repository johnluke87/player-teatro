using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CuePlayer.Audio;

public enum PlayState { Stopped, Playing, Paused }

/// <param name="FadingOut">long fade towards silence, will stop at the end</param>
/// <param name="FadingIn">long fade rising</param>
/// <param name="Ducking">long fade going down to a background level (keeps playing)</param>
/// <param name="Level">current fade level 0..1 (1 = full; below 1 when sitting in the background)</param>
public readonly record struct VoiceStatus(PlayState State, TimeSpan Position, bool FadingOut, bool FadingIn,
    bool Ducking, float Level, bool Failed, TimeSpan Start = default, TimeSpan End = default);

/// <summary>
/// One track. The compressed file lives in memory and is decoded on the fly by the
/// audio thread. All state is guarded by the engine lock.
/// </summary>
public sealed class Voice : IDisposable
{
    /// <summary>Tiny ramp used on start/pause/stop so nothing ever clicks.</summary>
    internal const double ClickRampSeconds = 0.012;

    private enum FadeEnd { None, Pause, Stop }

    private readonly AudioEngine _engine;
    private readonly WaveStream _reader;
    private ISampleProvider _chain = null!;
    private float[] _tmp = new float[8192];

    private PlayState _state = PlayState.Stopped;
    private float _gainTarget = 1f, _gainCurrent = 1f;
    private float _fade = 1f, _fadeTarget = 1f, _fadeStep;
    private FadeEnd _fadeEnd = FadeEnd.None;
    private bool _longRamp; // a real fade (not the 12 ms anti-click ramp) is in progress
    // false: squared curve (natural single fade-out); true: equal-power sine curve (crossfades, no dip in the middle)
    private bool _equalPower;
    private bool _loop;
    private bool _failed;
    private bool _disposed;
    private readonly Equalizer _eq;

    // trim: the track plays from _start to _end (null = file end), optionally fading out before _end
    private TimeSpan _start = TimeSpan.Zero;
    private TimeSpan? _end;
    private double _endFade;
    private bool _autoFading;

    public TimeSpan Duration { get; }

    internal Voice(AudioEngine engine, WaveStream reader)
    {
        _engine = engine;
        _reader = reader;
        Duration = reader.TotalTime;
        _eq = new Equalizer(Equalizer.TrackBands, engine.SampleRate);
        lock (engine.Sync) RebuildChain();
    }

    private object Sync => _engine.Sync;

    // ---------- public controls (UI thread) ----------

    /// <summary>Start (from where it is) or resume at full level. While fading, cancels the fade.</summary>
    public void Play()
    {
        lock (Sync)
        {
            if (_disposed || _failed) return;
            if (_state != PlayState.Playing) { _fade = 0f; _equalPower = false; }
            _state = PlayState.Playing;
            _longRamp = false;
            StartRamp(1f, ClickRampSeconds, FadeEnd.None);
        }
    }

    /// <summary>Jump back to the beginning and play.</summary>
    public void PlayFromStart()
    {
        lock (Sync)
        {
            if (_disposed || _failed) return;
            SeekInternal(_start);
            _autoFading = false;
            _fade = 0f;
            _equalPower = false;
            _state = PlayState.Playing;
            _longRamp = false;
            StartRamp(1f, ClickRampSeconds, FadeEnd.None);
        }
    }

    /// <summary>Start, resume, or bring back from the background, rising to full level over <paramref name="seconds"/>.</summary>
    public void FadeIn(double seconds) => FadeTo(1.0, seconds);

    /// <summary>
    /// Moves smoothly (equal-power curve) to <paramref name="level"/> (0..1 of full) and stays there,
    /// still playing. Starts the track if it is stopped or paused. Level 0 = fade out and stop.
    /// </summary>
    public void FadeTo(double level, double seconds)
    {
        if (level <= 0) { FadeOut(seconds, crossfade: true); return; }
        lock (Sync)
        {
            if (_disposed || _failed) return;
            float target = MathF.Asin((float)Math.Clamp(level, 0, 1)) * 2f / MathF.PI;
            if (_state != PlayState.Playing) { _fade = 0f; _equalPower = true; }
            else SetCurve(true);
            _state = PlayState.Playing;
            if (!_engine.IsRunning || seconds <= 0)
            {
                _fade = _fadeTarget = target;
                _fadeEnd = FadeEnd.None;
                _longRamp = false;
                return;
            }
            _longRamp = seconds > 0.05;
            StartRamp(target, seconds, FadeEnd.None);
        }
    }

    public void Pause()
    {
        lock (Sync)
        {
            if (_state != PlayState.Playing) return;
            _longRamp = false;
            if (!_engine.IsRunning) { _state = PlayState.Paused; ResetFade(); return; }
            StartRamp(0f, ClickRampSeconds, FadeEnd.Pause);
        }
    }

    public void Stop()
    {
        lock (Sync)
        {
            if (_state == PlayState.Playing && _engine.IsRunning)
            {
                _longRamp = false;
                StartRamp(0f, ClickRampSeconds, FadeEnd.Stop);
            }
            else StopNow();
        }
    }

    /// <summary>Fade to silence over <paramref name="seconds"/>, then stop and rewind.
    /// <paramref name="crossfade"/> uses the equal-power curve to pair with a <see cref="FadeIn"/>.</summary>
    public void FadeOut(double seconds, bool crossfade = false)
    {
        lock (Sync)
        {
            if (_state != PlayState.Playing || !_engine.IsRunning || seconds <= 0) { StopNow(); return; }
            SetCurve(crossfade);
            _longRamp = true;
            StartRamp(0f, seconds, FadeEnd.Stop);
        }
    }

    /// <summary>
    /// Plays only from <paramref name="start"/> to <paramref name="end"/> (null = to the end of the file),
    /// fading out during the last <paramref name="endFadeSeconds"/> (0 = hard stop). Loop repeats start→end.
    /// </summary>
    public void SetTrim(TimeSpan start, TimeSpan? end, double endFadeSeconds)
    {
        lock (Sync)
        {
            if (start < TimeSpan.Zero) start = TimeSpan.Zero;
            if (start >= Duration) start = TimeSpan.Zero;
            if (end is { } e && (e <= start || e >= Duration)) end = null;
            _start = start;
            _end = end;
            _endFade = Math.Max(0, endFadeSeconds);
            if (_state == PlayState.Stopped && !_disposed && !_failed) SeekInternal(_start);
        }
    }

    /// <summary>Effective end of playback (trim end or file end).</summary>
    public TimeSpan End { get { lock (Sync) return _end ?? Duration; } }

    /// <summary>Track EQ band gain in dB (bands: <see cref="Equalizer.TrackBands"/>).</summary>
    public void SetEq(int band, double db)
    {
        lock (Sync) _eq.SetGain(band, db);
    }

    public void Seek(TimeSpan position)
    {
        lock (Sync)
        {
            if (_disposed || _failed) return;
            SeekInternal(position);
        }
    }

    /// <summary>Track volume as slider fraction 0..1 (perceptual curve applied).</summary>
    public void SetVolume(float fraction)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);
        lock (Sync) _gainTarget = fraction * fraction;
    }

    public bool Loop
    {
        get { lock (Sync) return _loop; }
        set { lock (Sync) _loop = value; }
    }

    public VoiceStatus GetStatus()
    {
        lock (Sync)
        {
            var pos = _disposed ? TimeSpan.Zero : SafePosition();
            bool ramp = _longRamp && _fade != _fadeTarget;
            return new VoiceStatus(_state, pos,
                FadingOut: ramp && _fadeEnd == FadeEnd.Stop,
                FadingIn: ramp && _fadeTarget > _fade,
                Ducking: ramp && _fadeEnd == FadeEnd.None && _fadeTarget < _fade,
                Level: _state == PlayState.Playing ? CurveGain(_fade) : 0f,
                Failed: _failed,
                Start: _start,
                End: _end ?? Duration);
        }
    }

    // ---------- internals (engine lock held) ----------

    internal void RebuildChain()
    {
        if (_disposed) return;
        ISampleProvider sp = _reader.ToSampleProvider();
        int ch = sp.WaveFormat.Channels;
        if (ch == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (ch > 2) sp = new FirstTwoChannels(sp);
        if (sp.WaveFormat.SampleRate != _engine.SampleRate)
            sp = new WdlResamplingSampleProvider(sp, _engine.SampleRate);
        _chain = sp;
        _eq.SetSampleRate(_engine.SampleRate);
    }

    /// <summary>Switches fade curve keeping the current loudness, so the change is inaudible.</summary>
    private void SetCurve(bool equalPower)
    {
        if (equalPower == _equalPower) return;
        float gain = CurveGain(_fade);
        float targetGain = CurveGain(_fadeTarget);
        _equalPower = equalPower;
        _fade = FromGain(gain);
        _fadeTarget = FromGain(targetGain);
    }

    private float FromGain(float gain)
    {
        gain = Math.Clamp(gain, 0f, 1f);
        return _equalPower ? MathF.Asin(gain) * 2f / MathF.PI : MathF.Sqrt(gain);
    }

    private float CurveGain(float fade) =>
        _equalPower ? MathF.Sin(fade * MathF.PI * 0.5f) : fade * fade;

    private TimeSpan SafePosition()
    {
        try
        {
            var t = _reader.CurrentTime;
            return t > Duration ? Duration : t;
        }
        catch { return TimeSpan.Zero; }
    }

    private void SeekInternal(TimeSpan position)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > Duration) position = Duration;
        _reader.CurrentTime = position;
        RebuildChain(); // drops the resampler's leftover samples
    }

    private void ResetFade()
    {
        _longRamp = false;
        _equalPower = false;
        _fade = _fadeTarget = 1f;
        _fadeStep = 0f;
        _fadeEnd = FadeEnd.None;
    }

    private void StartRamp(float target, double seconds, FadeEnd end)
    {
        _fadeTarget = target;
        _fadeEnd = end;
        float distance = Math.Abs(target - _fade);
        if (distance <= 0f)
        {
            _longRamp = false;
            if (end != FadeEnd.None) CompleteFade();
            return;
        }
        double samples = Math.Max(1.0, seconds * _engine.SampleRate);
        _fadeStep = (float)(distance / samples);
    }

    private void CompleteFade()
    {
        var end = _fadeEnd;
        _fadeEnd = FadeEnd.None;
        if (end == FadeEnd.Pause)
        {
            _state = PlayState.Paused;
            ResetFade();
        }
        else if (end == FadeEnd.Stop)
        {
            StopNow();
        }
    }

    private void StopNow()
    {
        _state = PlayState.Stopped;
        _autoFading = false;
        ResetFade();
        if (!_disposed && !_failed)
        {
            try { SeekInternal(_start); } catch { _failed = true; }
        }
    }

    internal void Fail()
    {
        _failed = true;
        _state = PlayState.Stopped;
        _longRamp = false;
    }

    /// <summary>Called by the engine on the audio thread with the lock held.</summary>
    internal void MixInto(float[] buffer, int offset, int count)
    {
        if (_state != PlayState.Playing || _disposed) return;
        if (_tmp.Length < count) _tmp = new float[count];

        int read = ReadSource(count, out bool ended);
        int frames = read / 2;
        _eq.Process(_tmp, 0, frames);
        float g0 = _gainCurrent, g1 = _gainTarget;
        float dg = frames > 0 ? (g1 - g0) / frames : 0f;
        float g = g0;

        for (int f = 0; f < frames; f++)
        {
            g += dg;
            if (_fade != _fadeTarget)
            {
                if (_fade < _fadeTarget)
                {
                    _fade += _fadeStep;
                    if (_fade >= _fadeTarget) _fade = _fadeTarget;
                }
                else
                {
                    _fade -= _fadeStep;
                    if (_fade <= _fadeTarget) _fade = _fadeTarget;
                }
                if (_fade == _fadeTarget)
                {
                    _longRamp = false;
                    if (_fadeEnd != FadeEnd.None)
                    {
                        _gainCurrent = g1;
                        CompleteFade();
                        return;
                    }
                }
            }
            float k = g * CurveGain(_fade);
            int i = offset + 2 * f;
            buffer[i] += _tmp[2 * f] * k;
            buffer[i + 1] += _tmp[2 * f + 1] * k;
        }
        _gainCurrent = g1;
        if (ended) { StopNow(); return; }
        CheckTrimEnd();
    }

    /// <summary>Trim end: start the automatic fade-out in time, then stop (or loop back to the trim start).</summary>
    private void CheckTrimEnd()
    {
        if (_end is not { } end || _state != PlayState.Playing) return;
        var pos = _reader.CurrentTime;
        if (pos >= end)
        {
            if (_loop) { SeekInternal(_start); _autoFading = false; }
            else StopNow();
            return;
        }
        if (!_loop && !_autoFading && _endFade > 0 && _fadeEnd == FadeEnd.None
            && pos >= end - TimeSpan.FromSeconds(_endFade))
        {
            _autoFading = true;
            SetCurve(false);
            _longRamp = true;
            StartRamp(0f, Math.Max(0.05, (end - pos).TotalSeconds), FadeEnd.Stop);
        }
    }

    private int ReadSource(int count, out bool ended)
    {
        ended = false;
        int total = 0;
        bool justLooped = false;
        while (total < count)
        {
            int n = _chain.Read(_tmp, total, count - total);
            if (n > 0)
            {
                total += n;
                justLooped = false;
                continue;
            }
            if (_loop && !justLooped)
            {
                SeekInternal(_start);
                justLooped = true;
                continue;
            }
            ended = true;
            break;
        }
        return total & ~1;
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (_disposed) return;
            _disposed = true;
            _state = PlayState.Stopped;
        }
        try { _reader.Dispose(); } catch { }
    }

    /// <summary>Downmix for multichannel files: keeps front left/right.</summary>
    private sealed class FirstTwoChannels : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly int _ch;
        private float[] _buf = Array.Empty<float>();
        public WaveFormat WaveFormat { get; }

        public FirstTwoChannels(ISampleProvider src)
        {
            _src = src;
            _ch = src.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int need = frames * _ch;
            if (_buf.Length < need) _buf = new float[need];
            int got = _src.Read(_buf, 0, need) / _ch;
            for (int f = 0; f < got; f++)
            {
                buffer[offset + 2 * f] = _buf[f * _ch];
                buffer[offset + 2 * f + 1] = _buf[f * _ch + 1];
            }
            return got * 2;
        }
    }
}
