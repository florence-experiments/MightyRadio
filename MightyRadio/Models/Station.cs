using System.Globalization;
using System.Text.Json.Serialization;

namespace MightyRadio.Models;

/// <summary>
/// A playable station. Used for Radio Browser results, scanner feeds and favourites.
/// JSON names match the Radio Browser API so results deserialize directly.
/// </summary>
public sealed class Station
{
    [JsonPropertyName("stationuuid")] public string StationUuid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("url_resolved")] public string UrlResolved { get; set; } = "";
    [JsonPropertyName("country")] public string Country { get; set; } = "";
    [JsonPropertyName("countrycode")] public string CountryCode { get; set; } = "";
    [JsonPropertyName("codec")] public string Codec { get; set; } = "";
    [JsonPropertyName("bitrate")] public int Bitrate { get; set; }
    [JsonPropertyName("tags")] public string Tags { get; set; } = "";
    [JsonPropertyName("votes")] public int Votes { get; set; }
    [JsonPropertyName("clickcount")] public int ClickCount { get; set; }

    // Mighty Radio extras (not part of Radio Browser; used for scanner feeds)
    /// <summary>"radio" (Radio Browser) or "scanner".</summary>
    [JsonPropertyName("mr_source")] public string Source { get; set; } = "radio";
    [JsonPropertyName("mr_category")] public string Category { get; set; } = "";
    [JsonPropertyName("mr_region")] public string Region { get; set; } = "";
    [JsonPropertyName("mr_notes")] public string Notes { get; set; } = "";
    /// <summary>True when this entry is a web page to open in the browser rather than a stream.</summary>
    [JsonPropertyName("mr_web")] public bool IsWebLink { get; set; }

    [JsonIgnore] public bool IsScanner => Source == "scanner";

    [JsonIgnore]
    public string StreamUrl => !string.IsNullOrWhiteSpace(UrlResolved) ? UrlResolved.Trim() : (Url ?? "").Trim();

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "(unnamed station)" : Name.Trim();

    [JsonIgnore]
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (IsScanner)
            {
                if (!string.IsNullOrWhiteSpace(Category)) parts.Add(Category);
                if (!string.IsNullOrWhiteSpace(Region)) parts.Add(Region);
                parts.Add(IsWebLink ? "opens in browser" : "live audio");
                return string.Join("  ·  ", parts);
            }
            var country = CountryNames.Get(CountryCode, Country);
            if (!string.IsNullOrWhiteSpace(country)) parts.Add(country);
            if (!string.IsNullOrWhiteSpace(Codec) && !Codec.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)) parts.Add(Codec);
            if (Bitrate > 0) parts.Add($"{Bitrate} kbps");
            if (Votes > 0) parts.Add($"♥ {Votes.ToString("N0", CultureInfo.GetCultureInfo("en-GB"))}");
            return string.Join("  ·  ", parts);
        }
    }

    [JsonIgnore]
    public string SubText
    {
        get
        {
            if (IsScanner) return string.IsNullOrWhiteSpace(Notes) ? "" : Notes.Trim();
            if (string.IsNullOrWhiteSpace(Tags)) return "";
            var tags = Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8);
            return string.Join(", ", tags);
        }
    }

    [JsonIgnore] public bool HasSubText => !string.IsNullOrWhiteSpace(SubText);

    /// <summary>Stable identity for favourites.</summary>
    [JsonIgnore]
    public string Key => !string.IsNullOrWhiteSpace(StationUuid) ? StationUuid : "url:" + StreamUrl;

    public override string ToString() => DisplayName;
}

public static class CountryNames
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Friendly English country name (e.g. "United Kingdom" for GB), falling back to the API's name.</summary>
    public static string Get(string? code, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(code)) return fallback?.Trim() ?? "";
        lock (Cache)
        {
            if (Cache.TryGetValue(code, out var n)) return n;
            try { n = new RegionInfo(code).EnglishName; }
            catch { n = fallback?.Trim() ?? code; }
            if (string.IsNullOrWhiteSpace(n)) n = fallback?.Trim() ?? code;
            Cache[code] = n;
            return n;
        }
    }
}

public sealed class CountryOption
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public int StationCount { get; init; }
    public override string ToString() => Name;
}

public sealed class SortOption
{
    public string Label { get; init; } = "";
    public string Order { get; init; } = "votes";
    public bool Reverse { get; init; } = true;
    public override string ToString() => Label;
}
