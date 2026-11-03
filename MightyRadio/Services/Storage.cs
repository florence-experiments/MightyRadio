using System.IO;
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
    public static string ScannerPath => Path.Combine(DataFolder, "stations-scanner.json");
    public static string BundledScannerPath => Path.Combine(AppContext.BaseDirectory, "stations-scanner.json");
    public static string LogPath => Path.Combine(DataFolder, "error.log");

    public static string RecordingsFolder
    {
        get
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, "Music", "MightyRadio");
        }
    }

    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback();
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback();
        }
        catch (Exception ex)
        {
            // Corrupt file: keep a copy for inspection and start fresh rather than crash.
            Log(ex, "Loading " + path);
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* ignore */ }
            return fallback();
        }
    }

    public static bool Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log(ex, "Saving " + path);
            return false;
        }
    }

    public static List<Station> LoadFavourites() => Load(FavouritesPath, () => new List<Station>());
    public static void SaveFavourites(IEnumerable<Station> favs) => Save(FavouritesPath, favs.ToList());
    public static AppSettings LoadSettings() => Load(SettingsPath, () => new AppSettings());
    public static void SaveSettings(AppSettings s) => Save(SettingsPath, s);

    private static readonly object LogLock = new();

    public static void Log(Exception? ex, string context)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(DataFolder);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}
