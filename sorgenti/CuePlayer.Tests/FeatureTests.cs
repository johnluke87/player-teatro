using System.Text.Json;
using System.Windows.Threading;
using CuePlayer.Audio;
using CuePlayer.ViewModels;
using NAudio.Utils;
using NAudio.Wave;

namespace CuePlayer.Tests;

public class FeatureTests
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

    private static float Peak(AudioEngine e, double seconds)
    {
        var buf = new float[960 * 2];
        float p = 0;
        for (int b = 0; b < (int)(seconds / 0.02); b++)
        {
            e.Read(buf, 0, buf.Length);
            foreach (var v in buf) p = MathF.Max(p, MathF.Abs(v));
        }
        return p;
    }

    [Fact]
    public void Limiter_keeps_the_sum_under_minus_1_dBFS()
    {
        var e = new AudioEngine { IsRunning = true };
        var a = e.CreateVoice(AudioLoader.Open(Sine(3, 440, 0.7f), ".wav"));
        var b = e.CreateVoice(AudioLoader.Open(Sine(3, 440, 0.7f), ".wav"));
        a.Play(); b.Play();
        Assert.True(Peak(e, 0.5) >= 0.99f, "without limiter the sum clips at full scale");
        e.Limiter = true;
        Peak(e, 0.05);
        Assert.InRange(Peak(e, 0.5), 0.80f, 0.892f);
    }

    [Fact]
    public void Normalization_gain_scales_the_track()
    {
        var e = new AudioEngine { IsRunning = true };
        var v = e.CreateVoice(AudioLoader.Open(Sine(3, 440, 0.4f), ".wav"));
        v.Play();
        float before = Peak(e, 0.3);
        v.SetNormalization(0.5f);
        Peak(e, 0.05);
        Assert.InRange(Peak(e, 0.3) / before, 0.48, 0.52);
    }

    [Fact]
    public void Analysis_measures_peaks_and_loudness()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("rtc-an").FullName, "s.wav");
        File.WriteAllBytes(path, Sine(4, 440, 0.5f));
        var a = AudioAnalysis.Analyze(path);
        Assert.Equal(AudioAnalysis.Buckets, a.Peaks.Length);
        Assert.All(a.Peaks.Take(AudioAnalysis.Buckets - 2), p => Assert.InRange(p, 0.45f, 0.51f));
        Assert.InRange(a.LoudnessDb, -9.5, -8.5);   // sine RMS = amp/√2 → -9 dB
        Assert.InRange(a.PeakDb, -6.5, -5.5);
        Assert.InRange(a.NormalizationDb, -9.5, -8.5); // brought down to -18 dB RMS
    }

    // ------------------------------------------------------------------
    // integration: the real MainViewModel, no sound card (fades apply at once)
    // ------------------------------------------------------------------

    private static void RunSta(Action<Dispatcher> body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            var d = Dispatcher.CurrentDispatcher;
            d.BeginInvoke(() =>
            {
                try { body(d); }
                catch (Exception ex) { error = ex; }
                finally { d.InvokeShutdown(); }
            });
            Dispatcher.Run();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception(error.Message, error);
    }

    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void Chain_runs_the_next_command_by_itself_and_undo_goes_back()
    {
        var dir = Directory.CreateTempSubdirectory("rtc-show").FullName;
        Environment.SetEnvironmentVariable("RTCSTAGE_SETTINGS_DIR", Path.Combine(dir, "_settings"));
        File.WriteAllBytes(Path.Combine(dir, "01_MusicaA.wav"), Sine(30));
        File.WriteAllBytes(Path.Combine(dir, "02_MusicaB.wav"), Sine(30, 660));
        var show = new ShowSettings();
        show.Commands.Add(new ShowCommand { Name = "Parte MusicaA", Actions = { new CueAction { File = "01_MusicaA.wav", Kind = "Play", Seconds = 0 } } });
        show.Commands.Add(new ShowCommand
        {
            Name = "Entra MusicaB, MusicaA sotto", Group = "Atto I", AutoNext = true, AutoNextSeconds = 0.4,
            Actions =
            {
                new CueAction { File = "02_MusicaB.wav", Kind = "Play", Seconds = 0 },
                new CueAction { File = "01_MusicaA.wav", Kind = "Volume", Level = 20, Seconds = 2 },
            },
        });
        show.Commands.Add(new ShowCommand
        {
            Name = "Via MusicaB, torna MusicaA", Group = "Atto I",
            Actions =
            {
                new CueAction { File = "02_MusicaB.wav", Kind = "Stop", Seconds = 0 },
                new CueAction { File = "01_MusicaA.wav", Kind = "Volume", Level = 100, Seconds = 2 },
            },
        });
        Assert.True(show.Save(dir));

        RunSta(d =>
        {
            var vm = new MainViewModel(d);
            vm.OpenFolder(dir);
            for (int i = 0; i < 50 && vm.Cues.Any(c => !c.IsReady); i++) Pump(TimeSpan.FromMilliseconds(100));
            var a = vm.Cues[0].Voice!;
            var b = vm.Cues[1].Voice!;

            vm.RunByNumber(1);
            Assert.Equal(PlayState.Playing, a.GetStatus().State);

            vm.RunByNumber(2);
            Assert.Equal(PlayState.Playing, b.GetStatus().State);
            Assert.InRange(a.GetStatus().Level, 0.19, 0.21);
            Assert.True(vm.HasChain, "countdown to command 3 is running");

            Pump(TimeSpan.FromMilliseconds(800)); // the chain fires command 3 by itself
            Assert.False(vm.HasChain);
            Assert.Equal(PlayState.Stopped, b.GetStatus().State);
            Assert.InRange(a.GetStatus().Level, 0.99, 1.0);
            Assert.Equal("Via MusicaB, torna MusicaA", vm.LastCommand!.Name);

            vm.Undo(); // back to after command 2: B playing again, A under
            Assert.Equal(PlayState.Playing, b.GetStatus().State);
            Assert.InRange(a.GetStatus().Level, 0.19, 0.21);
            Assert.Equal("Via MusicaB, torna MusicaA", vm.NextCommand!.Name);

            vm.Undo(); // back to after command 1
            Assert.Equal(PlayState.Stopped, b.GetStatus().State);
            Assert.InRange(a.GetStatus().Level, 0.99, 1.0);
            Assert.Equal("Entra MusicaB, MusicaA sotto", vm.NextCommand!.Name);

            Assert.Contains("Atto I", vm.KnownGroups);
            vm.Dispose();
        });
        Environment.SetEnvironmentVariable("RTCSTAGE_SETTINGS_DIR", null);
    }

    [Fact]
    public void Backup_zip_contains_music_and_settings_and_restores()
    {
        var dir = Directory.CreateTempSubdirectory("rtc-bk").FullName;
        Environment.SetEnvironmentVariable("RTCSTAGE_SETTINGS_DIR", Path.Combine(dir, "_settings"));
        var src = Path.Combine(dir, "show"); Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "MusicaA.wav"), Sine(1));
        File.WriteAllText(Path.Combine(src, "appunti.txt"), "non va nel backup");
        new ShowSettings { MasterVolume = 77 }.Save(src);
        RunSta(d =>
        {
            var vm = new MainViewModel(d);
            vm.OpenFolder(src);
            var zip = Path.Combine(dir, "backup.zip");
            vm.BackupTo(zip);
            using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
                Assert.Equal(new[] { "cueplayer.json", "MusicaA.wav" }, z.Entries.Select(e => e.Name).OrderBy(n => n).ToArray());
            var dst = Path.Combine(dir, "restored");
            vm.RestoreFrom(zip, dst);
            Assert.Equal(dst, vm.Folder);
            Assert.Equal(77, vm.MasterVolume);
            vm.Dispose();
        });
        Environment.SetEnvironmentVariable("RTCSTAGE_SETTINGS_DIR", null);
    }
}
