using System.Text.Json.Serialization;

namespace MightyRadio.Models;

/// <summary>One entry in stations-scanner.json.</summary>
public sealed class ScannerFeed
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "Other";
    [JsonPropertyName("region")] public string Region { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    /// <summary>"stream" (plays in the app) or "web" (opens in the browser).</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "stream";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    /// <summary>Optional, e.g. "MP3". Helps the recorder pick a file type.</summary>
    [JsonPropertyName("codec")] public string Codec { get; set; } = "";

    public Station ToStation() => new()
    {
        StationUuid = "scanner:" + Url.Trim(),
        Name = Name.Trim(),
        Url = Url.Trim(),
        UrlResolved = Url.Trim(),
        Source = "scanner",
        Category = string.IsNullOrWhiteSpace(Category) ? "Other" : Category.Trim(),
        Region = Region?.Trim() ?? "",
        Notes = Notes?.Trim() ?? "",
        Codec = Codec?.Trim() ?? "",
        IsWebLink = string.Equals(Type?.Trim(), "web", StringComparison.OrdinalIgnoreCase),
    };
}

public sealed class ScannerFile
{
    [JsonPropertyName("_readme")] public string? Readme { get; set; }
    [JsonPropertyName("feeds")] public List<ScannerFeed> Feeds { get; set; } = new();
}

public sealed class AppSettings
{
    public int Volume { get; set; } = 70;
    public Station? LastStation { get; set; }
    public string CountryCode { get; set; } = "GB";
    public string SortOrder { get; set; } = "votes";
    public double WindowWidth { get; set; } = 1040;
    public double WindowHeight { get; set; } = 760;
}
