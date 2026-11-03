using System.IO;
using System.Text;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MightyRadio.Models;

namespace MightyRadio.Services;

/// <summary>
/// Records a stream to %USERPROFILE%\Music\MightyRadio using a second, silent LibVLC player with a
/// stream-output chain. MP3 streams are copied byte-for-byte (no quality loss); anything else is
/// converted to Ogg Vorbis so the file always plays.
/// </summary>
public sealed class Recorder : IDisposable
{
    private readonly LibVLC _vlc;
    private readonly Dispatcher _ui;
    private MediaPlayer? _mp;
    private Media? _media;
    private int _session;
    private Task _startTask = Task.CompletedTask;

    public bool IsRecording => _mp != null;
    public string? FilePath { get; private set; }
    public DateTime StartedAt { get; private set; }

    /// <summary>Raised on the UI thread if VLC reports a problem while recording.</summary>
    public event Action<string>? Failed;

    public Recorder(LibVLC vlc, Dispatcher ui)
    {
        _vlc = vlc;
        _ui = ui;
    }

    public static bool IsMp3(Station s) =>
        s.Codec.Equals("MP3", StringComparison.OrdinalIgnoreCase) ||
        (Uri.TryCreate(s.StreamUrl, UriKind.Absolute, out var u) && u.AbsolutePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase));

    /// <summary>Builds the VLC sout chain. Public so it can be unit-checked.</summary>
    public static string BuildSout(string filePath, bool copyMp3)
    {
        // VLC accepts forward slashes on Windows and they avoid backslash-escaping rules in sout strings.
        var dst = filePath.Replace('\\', '/').Replace("\"", "");
        return copyMp3
            ? $"#std{{access=file,mux=raw,dst=\"{dst}\"}}"
            : $"#transcode{{acodec=vorb,ab=128,channels=2,samplerate=44100}}:std{{access=file,mux=ogg,dst=\"{dst}\"}}";
    }

    public static string SafeFileName(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '(' or ')' ? ch : '_');
        var s = sb.ToString().Trim().Trim('_');
        if (s.Length > 60) s = s[..60].Trim();
        return string.IsNullOrEmpty(s) ? "Recording" : s;
    }

    /// <summary>Starts recording; returns the file path. Throws if the folder can't be created.</summary>
    public string Start(Station station)
    {
        if (IsRecording) StopAsync().Wait(TimeSpan.FromSeconds(3));

        Directory.CreateDirectory(Storage.RecordingsFolder);
        bool mp3 = IsMp3(station);
        var file = Path.Combine(Storage.RecordingsFolder,
            $"{SafeFileName(station.DisplayName)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}{(mp3 ? ".mp3" : ".ogg")}");

        var media = new Media(_vlc, station.StreamUrl, FromType.FromLocation,
            ":sout=" + BuildSout(file, mp3), ":no-sout-video", ":no-sout-spu");
        var mp = new MediaPlayer(_vlc);
        int session = ++_session;
        mp.EncounteredError += (_, _) => _ui.BeginInvoke(() =>
        {
            if (session == _session && IsRecording) Failed?.Invoke("Recording stopped: the station couldn't be recorded.");
        });
        mp.EndReached += (_, _) => _ui.BeginInvoke(() =>
        {
            if (session == _session && IsRecording) Failed?.Invoke("Recording stopped: the station stopped sending audio.");
        });

        _mp = mp;
        _media = media;
        FilePath = file;
        StartedAt = DateTime.Now;
        // Play() only starts VLC's own threads, but keep the UI responsive regardless.
        _startTask = Task.Run(() => { try { mp.Play(media); } catch (Exception ex) { Storage.Log(ex, "Recorder play"); } });
        return file;
    }

    /// <summary>Stops recording. Returns the saved file and its size (0 bytes = nothing captured).</summary>
    public async Task<(string? Path, long Bytes)> StopAsync()
    {
        var mp = _mp;
        var media = _media;
        var path = FilePath;
        _mp = null;
        _media = null;
        _session++;
        if (mp == null) return (null, 0);

        var startTask = _startTask;
        await Task.Run(async () =>
        {
            try { await startTask.ConfigureAwait(false); } catch { } // never stop before Play() has run
            try { mp.Stop(); } catch (Exception ex) { Storage.Log(ex, "Recorder stop"); }
            try { media?.Dispose(); mp.Dispose(); } catch { }
        }).ConfigureAwait(false);

        long bytes = 0;
        try
        {
            if (path != null && File.Exists(path))
            {
                bytes = new FileInfo(path).Length;
                if (bytes == 0) File.Delete(path);
            }
        }
        catch { }
        return (path, bytes);
    }

    public void Dispose()
    {
        try { StopAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}
