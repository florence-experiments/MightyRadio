using System.Text.Json.Serialization;

namespace MightyRadio.Models;

/// <summary>One entry in stations-scanner.json. Fields may be null in hand-edited files.</summary>
public sealed class ScannerFeed
{
    [JsonPropertyName("name")] public string? Name { get; set; } = "";
    [JsonPropertyName("category")] public string? Category { get; set; } = "Other";
    [JsonPropertyName("region")] public string? Region { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; } = "";
    /// <summary>"stream" (plays in the app) or "web" (opens in the browser).</summary>
    [JsonPropertyName("type")] public string? Type { get; set; } = "stream";
    [JsonPropertyName("notes")] public string? Notes { get; set; } = "";
    /// <summary>Optional, e.g. "MP3". Informational.</summary>
    [JsonPropertyName("codec")] public string? Codec { get; set; } = "";

    public static string NormaliseCategory(string? c) => string.IsNullOrWhiteSpace(c) ? "Other" : c.Trim();

    /// <summary>Favourites key: category + URL, so the same link listed in two categories stays distinct.</summary>
    public static string MakeKey(string? category, string? url) => "scanner:" + NormaliseCategory(category) + "|" + (url ?? "").Trim();

    public Station ToStation()
    {
        var url = (Url ?? "").Trim();
        var category = NormaliseCategory(Category);
        return new Station
        {
            StationUuid = MakeKey(category, url),
            Name = (Name ?? "").Trim(),
            Url = url,
            UrlResolved = url,
            Source = "scanner",
            Category = category,
            Region = (Region ?? "").Trim(),
            Notes = (Notes ?? "").Trim(),
            Codec = (Codec ?? "").Trim(),
            IsWebLink = string.Equals(Type?.Trim(), "web", StringComparison.OrdinalIgnoreCase),
        };
    }
}

public sealed class ScannerFile
{
    [JsonPropertyName("_readme")] public string? Readme { get; set; }
    [JsonPropertyName("feeds")] public List<ScannerFeed?>? Feeds { get; set; } = new();
}

public sealed class AppSettings
{
    public int Volume { get; set; } = 70;
    public Station? LastStation { get; set; }
    public string CountryCode { get; set; } = "GB";
    public string SortOrder { get; set; } = "votes";
    public double WindowWidth { get; set; } = 1040;
    public double WindowHeight { get; set; } = 760;
    /// <summary>"Dark", "Brass" or "Phosphor".</summary>
    public string Theme { get; set; } = "Brass";
    /// <summary>Minimise to the notification area instead of the taskbar.</summary>
    public bool MinimiseToTray { get; set; }
    /// <summary>Keep running in the tray when the window is closed while recordings are scheduled.</summary>
    public bool CloseToTrayWhenScheduled { get; set; } = true;
    public int LastSleepMinutes { get; set; } = 30;
}
