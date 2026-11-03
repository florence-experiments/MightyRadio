using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MightyRadio.Models;

namespace MightyRadio.Services;

/// <summary>Reads and writes the small JSON files in %APPDATA%\MightyRadio.</summary>
public static class Storage
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string DataFolder
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData)) appData = AppContext.BaseDirectory;
            return Path.Combine(appData, "MightyRadio");
        }
    }

    public static string FavouritesPath => Path.Combine(DataFolder, "favourites.json");
    public static string SettingsPath => Path.Combine(DataFolder, "settings.json");
    public static string HistoryPath => Path.Combine(DataFolder, "history.json");
    public static string SchedulesPath => Path.Combine(DataFolder, "schedules.json");
    public static string ConsentPath => Path.Combine(DataFolder, "recording-consent.json");
    public static string ScannerPath => Path.Combine(DataFolder, "stations-scanner.json");
    public static string BundledScannerPath => Path.Combine(AppContext.BaseDirectory, "stations-scanner.json");
    public static string LogPath => Path.Combine(DataFolder, "error.log");

    // ---- Music folder (Known Folder, follows OneDrive / drive redirection) ----

    private static readonly Guid FolderIdMusic = new("4BD8D571-6D19-48D3-BE97-422220080E43");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    private static string? _musicFolder;

    public static string MusicFolder => _musicFolder ??= ResolveMusicFolder();

    private static string ResolveMusicFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(FolderIdMusic, 0, IntPtr.Zero, out var p) == 0 && p != IntPtr.Zero)
            {
                try
                {
                    var s = Marshal.PtrToStringUni(p);
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                finally { Marshal.FreeCoTaskMem(p); }
            }
        }
        catch (Exception ex) { Log(ex, "Known folder lookup"); }

        var mm = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (!string.IsNullOrWhiteSpace(mm)) return mm;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
    }

    public static string RecordingsFolder => Path.Combine(MusicFolder, "MightyRadio");

    // ---- JSON load/save ----

    public enum LoadStatus { Missing, Ok, Failed }

    /// <summary>
    /// Load a JSON file. "Missing" and "Failed" are kept apart so callers can refuse to overwrite a
    /// file they could not read (locked by antivirus / OneDrive, or hand-edited into invalid JSON).
    /// A failed file is copied to a timestamped .bad-yyyyMMdd-HHmmss file.
    /// </summary>
    public static (T Value, LoadStatus Status) TryLoad<T>(string path, Func<T> fallback)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return (fallback(), LoadStatus.Missing);
                var json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return (fallback(), LoadStatus.Missing);
                var v = JsonSerializer.Deserialize<T>(json, JsonOptions);
                return v is null ? (fallback(), LoadStatus.Missing) : (v, LoadStatus.Ok);
            }
            catch (IOException ex) // locked / sharing violation: worth a retry
            {
                last = ex;
                Thread.Sleep(150 * (attempt + 1));
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
                break;
            }
            catch (Exception ex) // bad JSON
            {
                last = ex;
                break;
            }
        }

        Log(last, "Loading " + path);
        try
        {
            if (File.Exists(path))
                File.Copy(path, $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: false);
        }
        catch (Exception ex) { Log(ex, "Backing up unreadable " + path); }
        return (fallback(), LoadStatus.Failed);
    }

    public static T Load<T>(string path, Func<T> fallback) => TryLoad(path, fallback).Value;

    /// <summary>Atomic write: temp file, flush to disk, then replace.</summary>
    public static bool Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log(ex, "Saving " + path);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            return false;
        }
    }

    // ---- Favourites (guarded against wiping after a failed read) ----

    private static bool _favouritesReadFailed;

    public static bool FavouritesReadFailed => _favouritesReadFailed;

    public static List<Station> LoadFavourites()
    {
        var (list, status) = TryLoad(FavouritesPath, () => new List<Station>());
        _favouritesReadFailed = status == LoadStatus.Failed;
        var clean = list.Where(s => s != null).ToList();
        foreach (var s in clean) MigrateScannerKey(s);
        return clean;
    }

    /// <summary>v1.0 keyed scanner favourites on the URL alone; v1.1 adds the category (M11).</summary>
    private static void MigrateScannerKey(Station s)
    {
        if (!s.IsScanner) return;
        var expected = Models.ScannerFeed.MakeKey(s.Category, s.Url);
        if (s.StationUuid != expected) s.StationUuid = expected;
    }

    /// <summary>
    /// Save favourites. If the file existed but could not be read at start-up, the first save keeps the
    /// unreadable original (it was already copied to .bad-…); saves are only allowed after an explicit edit.
    /// </summary>
    public static bool SaveFavourites(IEnumerable<Station> favs, bool userEdited)
    {
        if (_favouritesReadFailed && !userEdited) return false;
        var ok = Save(FavouritesPath, favs.ToList());
        if (ok) _favouritesReadFailed = false;
        return ok;
    }

    public static AppSettings LoadSettings() => Load(SettingsPath, () => new AppSettings()) ?? new AppSettings();
    public static void SaveSettings(AppSettings s) => Save(SettingsPath, s);

    // ---- Log ----

    private static readonly object LogLock = new();

    public static void Log(Exception? ex, string context)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(DataFolder);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1_000_000)
                    File.Move(LogPath, Path.Combine(DataFolder, "error.old.log"), overwrite: true);
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}
