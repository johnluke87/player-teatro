using NAudio.Wave;

namespace CuePlayer.Audio;

/// <summary>Reads a whole file into memory (so playback never touches the disk) and opens a decoder on it.</summary>
public static class AudioLoader
{
    public static readonly string[] Extensions =
        { ".mp3", ".wav", ".aif", ".aiff", ".m4a", ".aac", ".wma", ".flac" };

    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static WaveStream Open(string path)
    {
        byte[] data;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            data = new byte[fs.Length];
            fs.ReadExactly(data);
        }
        return Open(data, Path.GetExtension(path));
    }

    public static WaveStream Open(byte[] data, string extension)
    {
        string ext = extension.ToLowerInvariant();
        WaveStream? reader = null;
        try
        {
            reader = ext switch
            {
                ".wav" => new WaveFileReader(new MemoryStream(data, false)),
                ".mp3" => new Mp3FileReader(new MemoryStream(data, false)),
                ".aif" or ".aiff" => new AiffFileReader(new MemoryStream(data, false)),
                _ => null,
            };
            if (reader != null) Validate(reader);
        }
        catch
        {
            try { reader?.Dispose(); } catch { }
            reader = null;
        }

        // Windows Media Foundation: m4a/aac/wma/flac, and fallback for odd mp3/wav files
        if (reader == null)
        {
            reader = new StreamMediaFoundationReader(new MemoryStream(data, false));
            try { Validate(reader); }
            catch { reader.Dispose(); throw; }
        }
        return reader;
    }

    /// <summary>Decode a few ms now, so a broken file fails at load time, not on stage.</summary>
    private static void Validate(WaveStream reader)
    {
        if (reader.TotalTime <= TimeSpan.Zero) throw new InvalidDataException("File vuoto");
        var sp = reader.ToSampleProvider();
        var buf = new float[sp.WaveFormat.Channels * 1024];
        sp.Read(buf, 0, buf.Length);
        reader.Position = 0;
    }
}
