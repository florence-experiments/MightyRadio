using System.Text.Json.Serialization;

namespace Mighty.Directory;

/// <summary>One playable row in the channel list. A channel with several streams keeps a single URL (the highest quality).</summary>
public sealed class ChannelListing
{
    public ChannelListing(
        string id,
        string name,
        string countryCode,
        string countryName,
        IReadOnlyList<string> categoryIds,
        string categoriesText,
        string streamUrl,
        string? quality,
        string? userAgent,
        string? referrer,
        bool isAdult,
        string searchText)
    {
        Id = id;
        Name = name;
        CountryCode = countryCode;
        CountryName = countryName;
        CategoryIds = categoryIds;
        CategoriesText = categoriesText;
        StreamUrl = streamUrl;
        Quality = quality;
        UserAgent = userAgent;
        Referrer = referrer;
        IsAdult = isAdult;
        SearchText = searchText;
    }

    public string Id { get; }
    public string Name { get; }
    public string CountryCode { get; }
    public string CountryName { get; }
    public IReadOnlyList<string> CategoryIds { get; }
    public string CategoriesText { get; }
    public string StreamUrl { get; }
    public string? Quality { get; }
    /// <summary>Optional player user-agent from the directory. Null when missing or not a single line.</summary>
    public string? UserAgent { get; }
    /// <summary>Optional http/https referrer from the directory.</summary>
    public string? Referrer { get; }
    /// <summary>True when iptv-org marks the channel is_nsfw. Hidden unless the user asks for adult channels.</summary>
    public bool IsAdult { get; }

    public override string ToString() => Name;

    /// <summary>Lower-case name, alternative names and stream title, for filtering.</summary>
    internal string SearchText { get; }

    public string Details
    {
        get
        {
            var parts = new List<string>(3);
            if (CountryName.Length > 0) parts.Add(CountryName);
            if (CategoriesText.Length > 0) parts.Add(CategoriesText);
            if (!string.IsNullOrEmpty(Quality)) parts.Add(Quality);
            return string.Join(" · ", parts);
        }
    }
}

public sealed class CountryOption
{
    public CountryOption(string? code, string name)
    {
        Code = code;
        Name = name;
    }

    /// <summary>ISO-like iptv-org code (United Kingdom is "UK"). Null means "any country" when the UI adds that row.</summary>
    public string? Code { get; }
    public string Name { get; }
    public override string ToString() => Name;
}

public sealed class CategoryOption
{
    public CategoryOption(string? id, string name)
    {
        Id = id;
        Name = name;
    }

    public string? Id { get; }
    public string Name { get; }
    public override string ToString() => Name;
}

internal sealed class ChannelRecord
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("alt_names")] public List<string>? AltNames { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
    [JsonPropertyName("is_nsfw")] public bool IsNsfw { get; set; }
}

internal sealed class StreamRecord
{
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("quality")] public string? Quality { get; set; }
    [JsonPropertyName("user_agent")] public string? UserAgent { get; set; }
    [JsonPropertyName("referrer")] public string? Referrer { get; set; }
}

internal sealed class CountryRecord
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
}

internal sealed class CategoryRecord
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}
