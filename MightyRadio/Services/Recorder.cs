using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MightyRadio.Models;

namespace MightyRadio.Services;

/// <summary>
/// Records a stream into the user's Music\MightyRadio folder without re-encoding.
///
/// * Normal Icecast/Shoutcast streams (MP3, AAC, Ogg, Opus, FLAC…) are saved byte-for-byte with VLC's
///   "dump" demuxer, then the file extension is corrected from the file's own header when it stops.
/// * HLS (.m3u8) streams are remuxed losslessly into an MPEG-TS (.ts) file.
/// * .pls / .m3u playlists are expanded first, so the real stream is recorded.
///
/// The recorder uses its own silent connection to the station. Recording the exact connection the player
/// uses would need a playback restart every time recording starts or stops, which interrupts listening;
/// a second connection was chosen deliberately. When the player has already resolved a playlist, the
/// resolved URL is reused.
/// </summary>
public sealed class Recorder : IDisposable
{
    private readonly LibVLC _vlc;
    private readonly Dispatcher _ui;
    private MediaPlayer? _mp;
    private Media? _media;
    private DispatcherTimer? _zeroCheck;
    private int _session;
    private bool _starting;
    private Task _startTask = Task.CompletedTask;

    public bool IsRecording => _starting || _mp != null;
    public string? FilePath { get; private set; }
    public DateTime StartedAt { get; private set; }
    public Station? Station { get; private set; }

    /// <summary>Raised on the UI thread if VLC reports a problem while recording.</summary>
    public event Action<string>? Failed;

    public Recorder(LibVLC vlc, Dispatcher ui)
    {
        _vlc = vlc;
        _ui = ui;
    }

    public enum Mode { Dump, Hls }

    public sealed record Plan(string Url, Mode Mode, string Extension);

    public static string SafeFileName(string? name)
    {
        var sb = new StringBuilder();
        foreach (var ch in (name ?? "").Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '(' or ')' ? ch : '_');
        var s = sb.ToString().Trim().Trim('_');
        if (s.Length > 60) s = s[..60].Trim();
        return string.IsNullOrEmpty(s) ? "Recording" : s;
    }

    /// <summary>VLC sout chain for HLS. Public so it can be unit-checked.</summary>
    public static string BuildHlsSout(string filePath)
    {
        // Forward slashes avoid backslash-escaping rules in sout strings and work on Windows.
        var dst = filePath.Replace('\\', '/').Replace("\"", "");
        return $"#std{{access=file,mux=ts,dst=\"{dst}\"}}";
    }

    /// <summary>Starts recording. Returns the file path, or null if StopAsync was called meanwhile.</summary>
    public async Task<string?> StartAsync(Station station, string? preferredUrl = null)
    {
        if (IsRecording) await StopAsync().ConfigureAwait(true);

        var url = StreamUrl.IsPlayable(preferredUrl) ? preferredUrl!.Trim() : station.StreamUrl;
        if (!StreamUrl.IsPlayable(url)) throw new InvalidOperationException("This station doesn't have a valid http/https web address.");

        int session = ++_session;
        _starting = true;
        Station = station;
        StartedAt = DateTime.Now;
        FilePath = null;
        try
        {
            var plan = await PlanAsync(url, station).ConfigureAwait(true);
            if (session != _session) return null; // stopped while probing

            Directory.CreateDirectory(Storage.RecordingsFolder);
            var file = UniquePath(Path.Combine(Storage.RecordingsFolder,
                $"{SafeFileName(station.DisplayName)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}{plan.Extension}"));

            Media media = plan.Mode == Mode.Hls
                ? new Media(_vlc, plan.Url, FromType.FromLocation, ":sout=" + BuildHlsSout(file), ":no-sout-video", ":no-sout-spu", ":sout-keep")
                : new Media(_vlc, plan.Url, FromType.FromLocation, ":demux=dump", ":demuxdump-file=" + file, ":no-video");
            var mp = new MediaPlayer(_vlc);
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
            _startTask = Task.Run(() => { try { mp.Play(media); } catch (Exception ex) { Storage.Log(ex, "Recorder play"); } });

            // A recording that never receives a byte shows a ticking timer forever otherwise.
            var check = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromSeconds(15) };
            check.Tick += (_, _) =>
            {
                check.Stop();
                if (session != _session || !IsRecording) return;
                long len = 0;
                try { if (File.Exists(file)) len = new FileInfo(file).Length; } catch { }
                if (len == 0) Failed?.Invoke("Recording stopped: no audio arrived after 15 seconds. The station may not allow a second connection.");
            };
            _zeroCheck = check;
            check.Start();
            return file;
        }
        finally
        {
            if (session == _session) _starting = false;
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    /// <summary>Stops recording. Returns the saved file and its size (0 bytes = nothing captured, file removed).</summary>
    public async Task<(string? Path, long Bytes)> StopAsync()
    {
        var mp = _mp;
        var media = _media;
        var path = FilePath;
        _mp = null;
        _media = null;
        _starting = false;
        _session++;
        _zeroCheck?.Stop();
        _zeroCheck = null;
        if (mp == null) return (null, 0);

        var startTask = _startTask;
        await Task.Run(async () =>
        {
            try { await startTask.ConfigureAwait(false); } catch { } // never stop before Play() has run
            try { mp.Stop(); } catch (Exception ex) { Storage.Log(ex, "Recorder stop"); }
            try { media?.Dispose(); mp.Dispose(); } catch { }
        }).ConfigureAwait(true);

        long bytes = 0;
        try
        {
            if (path != null && File.Exists(path))
            {
                bytes = new FileInfo(path).Length;
                if (bytes == 0) File.Delete(path);
                else path = FixExtension(path);
            }
        }
        catch (Exception ex) { Storage.Log(ex, "Finishing recording"); }
        FilePath = path;
        return (path, bytes);
    }

    public async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        var stop = StopAsync();
        var winner = await Task.WhenAny(stop, Task.Delay(timeout)).ConfigureAwait(true);
        return winner == stop;
    }

    public void Dispose() => _ = StopAsync();

    // ---------------- Planning: playlist / HLS detection ----------------

    private static async Task<Plan> PlanAsync(string url, Station station, int depth = 0)
    {
        var lower = url.ToLowerInvariant();
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath.ToLowerInvariant() : lower;
        string? contentType = null;
        byte[] head = Array.Empty<byte>();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await AppInfo.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            contentType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (resp.IsSuccessStatusCode)
            {
                await using var s = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                var buf = new byte[16 * 1024];
                int total = 0;
                // Playlists are small text files; audio streams are endless, so read only a little.
                int want = IsPlaylistHint(path, contentType) ? buf.Length : 4096;
                while (total < want)
                {
                    int n = await s.ReadAsync(buf.AsMemory(total, want - total), cts.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    total += n;
                }
                head = buf[..total];
            }
        }
        catch (Exception ex)
        {
            // Some Shoutcast v1 servers answer "ICY 200 OK", which HttpClient rejects. VLC copes, so just dump.
            Storage.Log(ex, "Recorder probe " + StreamUrl.ForLog(url));
        }

        var text = LooksLikeText(head) ? Encoding.UTF8.GetString(head) : "";
        bool hls = text.Contains("#EXT-X-", StringComparison.OrdinalIgnoreCase)
                   || (head.Length == 0 && station.Hls == 1 && path.EndsWith(".m3u8"));
        if (hls) return new Plan(url, Mode.Hls, ".ts");

        if (depth < 2 && text.Length > 0 && (IsPlaylistHint(path, contentType) || text.TrimStart().StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith("#EXTM3U")))
        {
            var inner = FirstPlaylistEntry(text, url);
            if (inner != null) return await PlanAsync(inner, station, depth + 1).ConfigureAwait(false);
        }

        return new Plan(url, Mode.Dump, GuessExtension(contentType, head, station.Codec));
    }

    private static bool IsPlaylistHint(string path, string? contentType) =>
        path.EndsWith(".pls") || path.EndsWith(".m3u") || path.EndsWith(".m3u8") ||
        contentType is "audio/x-scpls" or "application/pls+xml" or "audio/x-mpegurl" or "audio/mpegurl" or "application/x-mpegurl" or "application/vnd.apple.mpegurl";

    private static bool LooksLikeText(byte[] b)
    {
        if (b.Length == 0) return false;
        int n = Math.Min(b.Length, 512);
        for (int i = 0; i < n; i++)
            if (b[i] == 0 || (b[i] < 9 && b[i] != 0)) return false;
        return true;
    }

    /// <summary>First http/https entry of a .pls or .m3u playlist (relative entries resolved against the playlist URL).</summary>
    public static string? FirstPlaylistEntry(string text, string baseUrl)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('[')) continue;
            if (line.StartsWith("File", StringComparison.OrdinalIgnoreCase))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                line = line[(eq + 1)..].Trim();
            }
            else if (line.Contains('=') && !line.Contains("://")) continue; // Title1=…, NumberOfEntries=…
            if (Uri.TryCreate(new Uri(baseUrl), line, out var abs) && StreamUrl.IsPlayable(abs.AbsoluteUri))
                return abs.AbsoluteUri;
        }
        return null;
    }

    private static string GuessExtension(string? contentType, byte[] head, string? codec)
    {
        var sniffed = Sniff(head);
        if (sniffed != null) return sniffed;
        switch (contentType)
        {
            case "audio/mpeg": case "audio/mp3": case "audio/mpeg3": return ".mp3";
            case "audio/aac": case "audio/aacp": case "audio/x-aac": return ".aac";
            case "audio/ogg": case "application/ogg": case "audio/vorbis": return ".ogg";
            case "audio/opus": return ".opus";
            case "audio/flac": case "audio/x-flac": return ".flac";
            case "video/mp2t": return ".ts";
        }
        var c = (codec ?? "").Trim().ToUpperInvariant();
        if (c.Contains("AAC")) return ".aac";
        if (c.Contains("OGG") || c.Contains("VORBIS")) return ".ogg";
        if (c.Contains("OPUS")) return ".opus";
        if (c.Contains("FLAC")) return ".flac";
        return ".mp3";
    }

    /// <summary>Identify an audio container from its first bytes. Null if unknown.</summary>
    public static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4) return null;
        if (b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S')
        {
            var window = b[..Math.Min(b.Length, 256)];
            return window.IndexOf("OpusHead"u8) >= 0 ? ".opus" : ".ogg";
        }
        if (b[0] == 'f' && b[1] == 'L' && b[2] == 'a' && b[3] == 'C') return ".flac";
        if (b.Length >= 8 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p') return ".m4a";
        if (b[0] == 0x30 && b[1] == 0x26 && b[2] == 0xB2 && b[3] == 0x75) return ".wma";
        if (b[0] == 0x47 && b.Length > 188 && b[188] == 0x47) return ".ts";

        int start = 0;
        if (b.Length >= 10 && b[0] == 'I' && b[1] == 'D' && b[2] == '3')
        {
            int size = (b[6] & 0x7F) << 21 | (b[7] & 0x7F) << 14 | (b[8] & 0x7F) << 7 | (b[9] & 0x7F);
            start = 10 + size;
            if (start >= b.Length - 1) return ".mp3"; // ID3 tag bigger than our sample: almost always MP3
        }
        // Find the first frame sync (streams can start mid-frame).
        for (int i = start; i < Math.Min(b.Length - 1, start + 4096); i++)
        {
            if (b[i] != 0xFF || (b[i + 1] & 0xE0) != 0xE0) continue;
            int layer = (b[i + 1] >> 1) & 0x03;
            if ((b[i + 1] & 0xF0) == 0xF0 && layer == 0) return ".aac"; // ADTS
            if (layer != 0) return ".mp3";
        }
        return null;
    }

    /// <summary>Rename a finished recording so its extension matches what is really inside.</summary>
    private static string FixExtension(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".ts") return path;
            var buf = new byte[8192];
            int n;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                n = fs.Read(buf, 0, buf.Length);
            var real = Sniff(buf.AsSpan(0, n));
            if (real == null || real == ext) return path;
            var target = UniquePath(Path.ChangeExtension(path, real));
            File.Move(path, target);
            return target;
        }
        catch (Exception ex)
        {
            Storage.Log(ex, "Fixing recording extension");
            return path;
        }
    }
}
