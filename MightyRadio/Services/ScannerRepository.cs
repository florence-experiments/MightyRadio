using System.IO;
using System.Text;
using System.Text.Json;
using MightyRadio.Models;

namespace MightyRadio.Services;

/// <summary>
/// Loads the editable scanner feed list. On first run the bundled stations-scanner.json is copied to
/// %APPDATA%\MightyRadio so the user's edits survive app updates.
/// </summary>
public static class ScannerRepository
{
    public sealed record LoadResult(List<Station> Feeds, string? Problem);

    public static void EnsureUserCopy()
    {
        try
        {
            if (!File.Exists(Storage.ScannerPath) && File.Exists(Storage.BundledScannerPath))
            {
                Directory.CreateDirectory(Storage.DataFolder);
                File.Copy(Storage.BundledScannerPath, Storage.ScannerPath);
            }
        }
        catch (Exception ex) { Storage.Log(ex, "Copying scanner list"); }
    }

    public static LoadResult Load()
    {
        EnsureUserCopy();
        string? problem = null;
        var path = File.Exists(Storage.ScannerPath) ? Storage.ScannerPath : Storage.BundledScannerPath;
        var feeds = TryRead(path, out var error);
        if (feeds is null)
        {
            problem = $"Your scanner list has a mistake in it, so the built-in list is shown instead. ({error})";
            feeds = TryRead(Storage.BundledScannerPath, out _) ?? new List<ScannerFeed?>();
        }
        // Hand-edited lists can contain null entries or null fields; only http/https links are accepted.
        var stations = feeds
            .OfType<ScannerFeed>()
            .Where(f => StreamUrl.IsPlayable(f.Url))
            .Select(f => f.ToStation())
            .ToList();
        return new LoadResult(stations, problem);
    }

    private static List<ScannerFeed?>? TryRead(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path)) { error = "file not found"; return null; }
            var json = File.ReadAllText(path, Encoding.UTF8).TrimStart();
            // Accept either { "feeds": [...] } or a bare [...] array.
            if (json.StartsWith("[")) return JsonSerializer.Deserialize<List<ScannerFeed?>>(json, Storage.JsonOptions);
            return JsonSerializer.Deserialize<ScannerFile>(json, Storage.JsonOptions)?.Feeds;
        }
        catch (JsonException jex)
        {
            error = $"line {(jex.LineNumber ?? 0) + 1}";
            Storage.Log(jex, "Reading " + path);
            return null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Storage.Log(ex, "Reading " + path);
            return null;
        }
    }
}
