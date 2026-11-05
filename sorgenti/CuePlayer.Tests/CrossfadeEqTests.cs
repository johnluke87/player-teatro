using CuePlayer.Audio;
using NAudio.Utils;
using NAudio.Wave;

namespace CuePlayer.Tests;

public class CrossfadeEqTests
{
    private const int Rate = 48000;

    private static byte[] Sine(double seconds, float freq = 440, float amp = 0.5f)
    {
        var ms = new MemoryStream();
        using (var w = new WaveFileWriter(new IgnoreDisposeStream(ms), new WaveFormat(Rate, 16, 2)))
        {
            int frames = (int)(seconds * Rate);
            for (int i = 0; i < frames; i++)
            {
                float s = amp * MathF.Sin(2 * MathF.PI * freq * i / Rate);
                w.WriteSample(s);
                w.WriteSample(s);
            }
        }
        return ms.ToArray();
    }

    private static (AudioEngine, Voice) Make(byte[] wav)
    {
        var e = new AudioEngine { IsRunning = true };
        return (e, e.CreateVoice(AudioLoader.Open(wav, ".wav")));
    }

    /// <summary>Peak of each 20 ms block.</summary>
    private static List<float> Render(AudioEngine e, double seconds)
    {
        var peaks = new List<float>();
        var buf = new float[960 * 2];
        for (int b = 0; b < (int)Math.Round(seconds / 0.02); b++)
        {
            e.Read(buf, 0, buf.Length);
            peaks.Add(buf.Max(MathF.Abs));
        }
        return peaks;
    }

    [Fact]
    public void Crossfade_is_equal_power_with_no_dip()
    {
        // two separate engines so each voice can be measured on its own
        var (ea, a) = Make(Sine(10));
        var (eb, b) = Make(Sine(10));
        a.Play();
        float full = Render(ea, 0.3).Last();

        a.FadeOut(2, crossfade: true);
        b.FadeIn(2);
        Assert.True(b.GetStatus().FadingIn);
        var pa = Render(ea, 2.2);
        var pb = Render(eb, 2.2);

        // half way: both at ~0.707 of full (−3 dB each), total power constant
        Assert.InRange(pa[50] / full, 0.66, 0.75);
        Assert.InRange(pb[50] / full, 0.66, 0.75);
        for (int i = 5; i < 100; i++)
        {
            double power = (pa[i] * pa[i] + pb[i] * pb[i]) / (full * full);
            Assert.InRange(power, 0.9, 1.1);
        }
        Assert.Equal(PlayState.Stopped, a.GetStatus().State);
        Assert.Equal(PlayState.Playing, b.GetStatus().State);
        Assert.False(b.GetStatus().FadingIn);
        Assert.InRange(pb.Last() / full, 0.98, 1.02);
    }

    [Fact]
    public void A_to_B_with_A_in_background_then_back_to_A()
    {
        var (ea, a) = Make(Sine(20));
        var (eb, b) = Make(Sine(20));
        a.Play();
        float full = Render(ea, 0.3).Last();

        // B enters in 2 s, A drops to 20 % and keeps playing
        b.FadeIn(2);
        a.FadeTo(0.20, 2);
        Assert.True(a.GetStatus().Ducking);
        var pa = Render(ea, 2.2);
        Render(eb, 2.2);
        var sa = a.GetStatus();
        Assert.Equal(PlayState.Playing, sa.State);
        Assert.False(sa.Ducking);
        Assert.InRange(sa.Level, 0.19, 0.21);
        Assert.InRange(pa.Last() / full, 0.18, 0.22);
        for (int i = 1; i < pa.Count; i++) Assert.True(pa[i] <= pa[i - 1] + 0.01f, "smooth descent");
        var posA = sa.Position;

        // a few seconds later: back to A (from 20 % up to full), B fades out and stops
        Render(ea, 3); Render(eb, 3);
        Assert.True(a.GetStatus().Position > posA, "A kept running underneath");
        a.FadeIn(2);
        b.FadeOut(2, crossfade: true);
        var up = Render(ea, 2.2);
        Render(eb, 2.2);
        Assert.InRange(up[0] / full, 0.17, 0.3); // starts from the background level, no jump
        Assert.InRange(up.Last() / full, 0.98, 1.02);
        Assert.Equal(PlayState.Stopped, b.GetStatus().State);
        Assert.InRange(a.GetStatus().Level, 0.99, 1.0);
    }

    /// <summary>Typical show: 1) MusicaA parte  2) MusicaB parte + MusicaA volume 20%  3) MusicaB stop + MusicaA volume 100%
    /// 4) MusicaA stop + MusicaB parte subito.</summary>
    [Fact]
    public void Buttons_musicaA_and_musicaB()
    {
        var (ev, musicaA) = Make(Sine(30));
        var (ec, musicaB) = Make(Sine(30, 1000));
        void Do(Voice v, string kind, double level = 100, double secs = 0) =>
            CuePlayer.ViewModels.MainViewModel.Apply(v, new CueAction { Kind = kind, Level = level, Seconds = secs });
        void Both(double s) { Render(ev, s); Render(ec, s); }

        // button 1
        Do(musicaA, "Play");
        Both(0.3);
        float full = Render(ev, 0.1).Last();
        Assert.Equal(PlayState.Playing, musicaA.GetStatus().State);
        Assert.Equal(PlayState.Stopped, musicaB.GetStatus().State);

        // a Volume action on a track that is not playing must NOT start it
        Do(musicaB, "Volume", 20, 2);
        Both(0.2);
        Assert.Equal(PlayState.Stopped, musicaB.GetStatus().State);

        // button 2: MusicaB parte, MusicaA si abbassa al 20% in 2 s
        Do(musicaB, "Play");
        Do(musicaA, "Volume", 20, 2);
        Both(2.3);
        Assert.Equal(PlayState.Playing, musicaB.GetStatus().State);
        Assert.InRange(Render(ev, 0.1).Last() / full, 0.18, 0.22);
        Assert.Equal(PlayState.Playing, musicaA.GetStatus().State);

        // button 3: MusicaB stop, MusicaA torna al 100% in 2 s
        Do(musicaB, "Stop");
        Do(musicaA, "Volume", 100, 2);
        Both(2.3);
        Assert.Equal(PlayState.Stopped, musicaB.GetStatus().State);
        Assert.InRange(Render(ev, 0.1).Last() / full, 0.98, 1.02);

        // button 4: MusicaA stop, MusicaB parte subito
        Do(musicaA, "Stop");
        Do(musicaB, "Play");
        Both(0.2);
        Assert.Equal(PlayState.Stopped, musicaA.GetStatus().State);
        Assert.Equal(PlayState.Playing, musicaB.GetStatus().State);
        Assert.True(Render(ec, 0.1).Last() > 0.4f);
    }

    [Fact]
    public void Play_on_a_background_track_brings_it_to_full()
    {
        var (e, v) = Make(Sine(10));
        v.Play();
        float full = Render(e, 0.3).Last();
        v.FadeTo(0.3, 0.5);
        Render(e, 0.7);
        v.Play();
        Assert.InRange(Render(e, 0.1).Last() / full, 0.98, 1.02);
    }

    [Fact]
    public void Show_settings_round_trip_through_cueplayer_json()
    {
        var dir = Directory.CreateTempSubdirectory("cueplayer-json").FullName;
        try
        {
            var s = new ShowSettings { MasterVolume = 80, MasterEq = new[] { 3.0, 0, -2.5, 0, 6 } };
            s.Cues["B.mp3"] = new CueSettings { Volume = 70, Eq = new[] { 4.5, -1, 2 } };
            s.Commands.Add(new ShowCommand
            {
                Name = "B entra, A sotto",
                Color = "#9B5DE5",
                Actions =
                {
                    new CueAction { File = "B.mp3", Kind = "Play", Level = 100, Seconds = 3 },
                    new CueAction { File = "A.wav", Kind = "Play", Level = 20, Seconds = 3 },
                },
            });
            s.Commands.Add(new ShowCommand
            {
                Name = "Torna ad A",
                Actions =
                {
                    new CueAction { File = "A.wav", Kind = "Play", Level = 100, Seconds = 3 },
                    new CueAction { File = "B.mp3", Kind = "Stop", Seconds = 3 },
                },
            });
            Assert.True(s.Save(dir));
            var r = ShowSettings.Load(dir);
            Assert.Equal(80, r.MasterVolume);
            Assert.Equal(new[] { 3.0, 0, -2.5, 0, 6 }, r.MasterEq);
            var c = r.Cues["b.MP3"]; // file names are case-insensitive on Windows
            Assert.Equal(new[] { 4.5, -1, 2 }, c.Eq);
            Assert.Equal(2, r.Commands.Count);
            Assert.Equal("B entra, A sotto", r.Commands[0].Name);
            Assert.Equal("#9B5DE5", r.Commands[0].Color);
            Assert.Equal(20, r.Commands[0].Actions[1].Level);
            Assert.Equal("Stop", r.Commands[1].Actions[1].Kind);
            Assert.Equal("▶ B parte (sfumando in 3s)\n▶ A parte al 20% (sfumando in 3s)",
                CuePlayer.ViewModels.CommandText.Summary(r.Commands[0].Actions));
            var vol = new[] { new CueAction { File = "MusicaA.mp3", Kind = "Volume", Level = 20, Seconds = 2 },
                              new CueAction { File = "MusicaB.wav", Kind = "Stop", Seconds = 0 } };
            Assert.Equal("🔉 MusicaA → volume 20% in 2s\n⏹ MusicaB stop",
                CuePlayer.ViewModels.CommandText.Summary(vol));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Switching_a_running_fade_to_crossfade_does_not_jump()
    {
        var (e, v) = Make(Sine(10));
        v.Play();
        Render(e, 0.2);
        v.FadeOut(4);                 // normal squared fade
        var before = Render(e, 1.0);
        v.FadeOut(2, crossfade: true); // now part of a crossfade
        var after = Render(e, 0.1);
        Assert.True(Math.Abs(after[0] - before.Last()) < 0.03f, $"{before.Last()} → {after[0]}");
    }

    [Fact]
    public void FadeIn_from_paused_resumes_where_it_was()
    {
        var (e, v) = Make(Sine(10));
        v.Play();
        Render(e, 1.0);
        v.Pause();
        Render(e, 0.1);
        v.FadeIn(1);
        var p = Render(e, 1.2);
        Assert.True(p[2] < p[40], "rises");
        Assert.InRange(v.GetStatus().Position.TotalSeconds, 2.0, 2.4);
    }

    [Theory]
    [InlineData(0, 12, 80f, 3.0, 4.2)]    // bass +12 dB at 80 Hz → ~+11 dB
    [InlineData(1, 6, 1000f, 1.9, 2.1)]   // mid +6 dB at centre → ×2
    [InlineData(2, -12, 12000f, 0.22, 0.28)] // treble −12 dB at 12 kHz → ~×0.24 (shelf overshoot)
    [InlineData(2, 12, 100f, 0.97, 1.03)] // treble boost leaves the bass alone
    public void Track_eq_bands_shape_the_sound(int band, double db, float freq, double minRatio, double maxRatio)
    {
        var (e, v) = Make(Sine(3, freq, amp: 0.05f));
        v.Play();
        float flat = Render(e, 0.5).Last();
        v.SetEq(band, db);
        float eq = Render(e, 0.5).Last();
        Assert.InRange(eq / flat, minRatio, maxRatio);
    }

    [Fact]
    public void Master_eq_and_back_to_flat_is_transparent()
    {
        var (e, v) = Make(Sine(5, 250, amp: 0.1f));
        v.Play();
        float flat = Render(e, 0.4).Last();
        e.SetMasterEq(1, 6); // 250 Hz +6 dB
        Assert.InRange(Render(e, 0.4).Last() / flat, 1.9, 2.1);
        e.SetMasterEq(1, 0);
        Assert.InRange(Render(e, 0.4).Last() / flat, 0.999, 1.001);
    }

    [Fact]
    public void Eq_on_silence_stays_finite_and_silent()
    {
        var (e, v) = Make(Sine(1));
        e.SetMasterEq(0, 12);
        e.SetMasterEq(4, -12);
        v.SetEq(0, 12);
        v.Play();
        Render(e, 1.5); // plays then ends → silence through the filters
        var buf = new float[1920];
        for (int i = 0; i < 200; i++) e.Read(buf, 0, buf.Length);
        Assert.All(buf, s => Assert.True(float.IsFinite(s) && Math.Abs(s) < 1e-6));
    }
}
