using CuePlayer.Audio;
using NAudio.MediaFoundation;
using NAudio.Utils;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CuePlayer.Tests;

/// <summary>Drives the mixer by hand (no sound card) and measures what comes out.</summary>
public class EngineTests
{
    private const int Rate = 48000; // engine default without a device

    private static byte[] Sine(double seconds, int rate = 44100, int channels = 2, int bits = 16, float amp = 0.5f, float freq = 440)
    {
        var ms = new MemoryStream();
        var fmt = bits == 32 ? WaveFormat.CreateIeeeFloatWaveFormat(rate, channels) : new WaveFormat(rate, bits, channels);
        using (var w = new WaveFileWriter(new IgnoreDisposeStream(ms), fmt))
        {
            int frames = (int)(seconds * rate);
            for (int i = 0; i < frames; i++)
            {
                float s = amp * MathF.Sin(2 * MathF.PI * freq * i / rate);
                for (int c = 0; c < channels; c++) w.WriteSample(s);
            }
        }
        return ms.ToArray();
    }

    private static (AudioEngine engine, Voice voice) Make(byte[] wav, string ext = ".wav")
    {
        var engine = new AudioEngine { IsRunning = true };
        var voice = engine.CreateVoice(AudioLoader.Open(wav, ext));
        return (engine, voice);
    }

    /// <summary>Renders <paramref name="seconds"/> and returns the peak of each 20 ms block.</summary>
    private static List<float> Render(AudioEngine e, double seconds)
    {
        var peaks = new List<float>();
        var buf = new float[960 * 2];
        int blocks = (int)Math.Round(seconds / 0.02);
        for (int b = 0; b < blocks; b++)
        {
            e.Read(buf, 0, buf.Length);
            peaks.Add(buf.Max(MathF.Abs));
        }
        return peaks;
    }

    [Fact]
    public void Silent_until_played_then_sounds_immediately()
    {
        var (e, v) = Make(Sine(2));
        Assert.All(Render(e, 0.2), p => Assert.Equal(0f, p));
        v.Play();
        var p = Render(e, 0.1);
        Assert.True(p[0] > 0.3f, $"first 20 ms block should already be loud, was {p[0]}");
        Assert.Equal(PlayState.Playing, v.GetStatus().State);
    }

    [Fact]
    public void Pause_keeps_position_and_resume_continues()
    {
        var (e, v) = Make(Sine(3));
        v.Play();
        Render(e, 1.0);
        v.Pause();
        Render(e, 0.1);
        var st = v.GetStatus();
        Assert.Equal(PlayState.Paused, st.State);
        Assert.InRange(st.Position.TotalSeconds, 0.9, 1.2);
        Assert.All(Render(e, 0.3), p => Assert.Equal(0f, p));
        v.Play();
        Assert.True(Render(e, 0.1).Last() > 0.3f);
        Assert.True(v.GetStatus().Position.TotalSeconds > 1.0);
    }

    [Fact]
    public void Stop_silences_and_rewinds()
    {
        var (e, v) = Make(Sine(3));
        v.Play();
        Render(e, 1.0);
        v.Stop();
        var after = Render(e, 0.1);
        Assert.Equal(0f, after.Last());
        var st = v.GetStatus();
        Assert.Equal(PlayState.Stopped, st.State);
        Assert.Equal(0, st.Position.TotalSeconds, 2);
    }

    [Fact]
    public void FadeOut_goes_down_gradually_then_stops()
    {
        var (e, v) = Make(Sine(10));
        v.Play();
        Render(e, 0.5);
        v.FadeOut(2.0);
        Assert.True(v.GetStatus().FadingOut);
        var p = Render(e, 2.2);
        // 0.5 s in: still clearly audible; 1.5 s in: much quieter; end: silence
        Assert.True(p[25] > 0.2f, $"at 0.5s {p[25]}");
        Assert.True(p[75] < p[25] / 4, $"at 1.5s {p[75]} vs {p[25]}");
        Assert.Equal(0f, p.Last());
        // monotonic (never jumps back up)
        for (int i = 1; i < 100; i++) Assert.True(p[i] <= p[i - 1] + 0.01f, $"block {i}");
        Assert.Equal(PlayState.Stopped, v.GetStatus().State);
        // playing again starts from the top at full volume
        v.Play();
        Assert.True(Render(e, 0.1).Last() > 0.4f);
    }

    [Fact]
    public void Play_during_fade_cancels_it()
    {
        var (e, v) = Make(Sine(10));
        v.Play();
        Render(e, 0.2);
        v.FadeOut(3);
        Render(e, 1.5);
        v.Play();
        var p = Render(e, 0.2);
        Assert.True(p.Last() > 0.4f);
        Assert.False(v.GetStatus().FadingOut);
    }

    [Fact]
    public void Track_and_master_volume_scale_output()
    {
        var (e, v) = Make(Sine(3));
        v.Play();
        float full = Render(e, 0.3).Last();
        v.SetVolume(0.5f); // 50% slider → 0.25 gain
        float half = Render(e, 0.3).Last();
        Assert.InRange(half / full, 0.22, 0.28);
        v.SetVolume(1f);
        e.SetMasterVolume(0f);
        Assert.Equal(0f, Render(e, 0.2).Last());
    }

    [Fact]
    public void Natural_end_stops_and_rewinds()
    {
        var (e, v) = Make(Sine(0.5));
        v.Play();
        Render(e, 0.8);
        var st = v.GetStatus();
        Assert.Equal(PlayState.Stopped, st.State);
        Assert.Equal(0, st.Position.TotalSeconds, 2);
    }

    [Fact]
    public void Loop_keeps_playing_past_the_end()
    {
        var (e, v) = Make(Sine(0.5));
        v.Loop = true;
        v.Play();
        var p = Render(e, 2.0);
        Assert.Equal(PlayState.Playing, v.GetStatus().State);
        Assert.True(p.Skip(5).All(x => x > 0.3f), "no gaps at the loop point");
    }

    [Fact]
    public void Several_tracks_mix_together()
    {
        var engine = new AudioEngine { IsRunning = true };
        var a = engine.CreateVoice(AudioLoader.Open(Sine(2, amp: 0.3f), ".wav"));
        var b = engine.CreateVoice(AudioLoader.Open(Sine(2, amp: 0.3f), ".wav"));
        a.Play();
        float one = Render(engine, 0.2).Last();
        b.PlayFromStart();
        Render(engine, 0.2);
        Assert.Equal(PlayState.Playing, a.GetStatus().State);
        Assert.Equal(PlayState.Playing, b.GetStatus().State);
        Assert.True(one > 0.25f);
    }

    [Fact]
    public void Resampled_position_tracks_real_time()
    {
        var (e, v) = Make(Sine(5, rate: 44100));
        v.Play();
        Render(e, 2.0);
        Assert.InRange(v.GetStatus().Position.TotalSeconds, 1.95, 2.1);
    }

    [Fact]
    public void Seek_jumps()
    {
        var (e, v) = Make(Sine(10));
        v.Seek(TimeSpan.FromSeconds(7));
        v.Play();
        Render(e, 0.5);
        Assert.InRange(v.GetStatus().Position.TotalSeconds, 7.4, 7.6);
    }

    [Theory]
    [InlineData(1, 16, 22050)]
    [InlineData(2, 24, 48000)]
    [InlineData(2, 32, 96000)]
    [InlineData(6, 16, 48000)]
    public void Wav_variants_play(int channels, int bits, int rate)
    {
        var (e, v) = Make(Sine(1, rate, channels, bits));
        v.Play();
        Assert.True(Render(e, 0.2).Last() > 0.3f);
    }

    [Fact]
    public void Compressed_formats_decode_from_memory()
    {
        MediaFoundationApi.Startup();
        var dir = Directory.CreateTempSubdirectory("cueplayer-test").FullName;
        try
        {
            var src = Path.Combine(dir, "src.wav");
            File.WriteAllBytes(src, Sine(3));
            var mp3 = Path.Combine(dir, "t.mp3");
            var m4a = Path.Combine(dir, "t.m4a");
            using (var r = new WaveFileReader(src)) MediaFoundationEncoder.EncodeToMp3(r, mp3, 192000);
            using (var r = new WaveFileReader(src)) MediaFoundationEncoder.EncodeToAac(r, m4a, 192000);

            foreach (var f in new[] { mp3, m4a })
            {
                var engine = new AudioEngine { IsRunning = true };
                var v = engine.CreateVoice(AudioLoader.Open(f));
                Assert.InRange(v.Duration.TotalSeconds, 2.9, 3.2);
                v.Play();
                var p = Render(engine, 1.0);
                Assert.True(p.Skip(10).All(x => x > 0.3f), $"{Path.GetExtension(f)} decodes");
                v.FadeOut(0.5);
                Render(engine, 0.7);
                Assert.Equal(PlayState.Stopped, v.GetStatus().State);
                v.Play();
                Assert.True(Render(engine, 0.3).Last() > 0.3f, $"{Path.GetExtension(f)} replays after stop");
                engine.Dispose();
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Reads the engine exactly like WasapiOut does: through SampleToWaveProvider into one reused byte[].</summary>
    private static float[] RenderLikeWasapi(AudioEngine e, double seconds, byte[] reused)
    {
        var wave = new SampleToWaveProvider(e);
        var outp = new List<float>();
        int total = (int)(seconds * Rate) * 2 * 4;
        for (int done = 0; done < total; done += reused.Length)
        {
            int n = wave.Read(reused, 0, reused.Length);
            for (int i = 0; i < n; i += 4) outp.Add(BitConverter.ToSingle(reused, i));
        }
        return outp.ToArray();
    }

    [Fact]
    public void Output_through_wasapi_buffer_is_identical_to_the_file()
    {
        // regression: Array.Clear on NAudio's byte-backed float buffer left 3/4 of old audio → echo
        var (e, v) = Make(Sine(3, rate: Rate));
        var reused = new byte[480 * 2 * 4]; // 10 ms, as in WASAPI event mode
        v.Play();
        var got = RenderLikeWasapi(e, 1.0, reused);
        var reference = new float[got.Length];
        new WaveFileReader(new MemoryStream(Sine(3, rate: Rate))).ToSampleProvider().Read(reference, 0, reference.Length);
        // after the 12 ms anti-click ramp the output must match the file sample by sample
        for (int i = Rate / 20 * 2; i < got.Length; i++)
            Assert.True(Math.Abs(got[i] - reference[i]) < 1e-4, $"sample {i}: {got[i]} vs {reference[i]}");

        v.Stop();
        RenderLikeWasapi(e, 0.05, reused);
        Assert.All(RenderLikeWasapi(e, 0.2, reused), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Garbage_file_is_rejected_at_load_time()
    {
        var junk = new byte[5000];
        new Random(1).NextBytes(junk);
        Assert.ThrowsAny<Exception>(() => AudioLoader.Open(junk, ".mp3"));
    }

    [Fact]
    public void Removing_a_voice_while_mixing_is_safe()
    {
        var (e, v) = Make(Sine(3));
        v.Play();
        var t = Task.Run(() => Render(e, 2.0));
        Thread.Sleep(5);
        e.RemoveVoice(v);
        t.Wait();
    }
}
