using System.Text.Json;

namespace Mighty.Directory;

/// <summary>
/// Joined iptv-org directory. Every country and category in the source files is kept.
/// Rows are channels (or stray streams) that have an http/https URL; nothing else is dropped
/// (closed and NSFW channels stay). This type does not touch WPF.
/// </summary>
public sealed class ChannelCatalog
{
    public const string UkCountryCode = "UK";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public ChannelCatalog(
        IReadOnlyList<ChannelListing> channels,
        IReadOnlyList<CountryOption> countries,
        IReadOnlyList<CategoryOption> categories)
    {
        Channels = channels;
        Countries = countries;
        Categories = categories;
    }

    public IReadOnlyList<ChannelListing> Channels { get; }
    public IReadOnlyList<CountryOption> Countries { get; }
    public IReadOnlyList<CategoryOption> Categories { get; }

    /// <summary>
    /// Reads the four iptv-org JSON arrays. Unknown properties are ignored so a new field upstream
    /// does not break loading. Throws <see cref="InvalidDataException"/> when a file is not a JSON array.
    /// </summary>
    public static ChannelCatalog Parse(Stream channels, Stream streams, Stream countries, Stream categories)
    {
        var channelRows = Read<ChannelRecord>(channels, "channels");
        var streamRows = Read<StreamRecord>(streams, "streams");
        var countryRows = Read<CountryRecord>(countries, "countries");
        var categoryRows = Read<CategoryRecord>(categories, "categories");
        return Build(channelRows, streamRows, countryRows, categoryRows);
    }

    /// <summary>
    /// Name is a case-insensitive substring of the channel name or an alternative name.
    /// Blank country or category means no filter on that axis.
    /// </summary>
    public IReadOnlyList<ChannelListing> Filter(string? name, string? countryCode, string? categoryId)
    {
        var nameQuery = string.IsNullOrWhiteSpace(name) ? null : name.Trim().ToLowerInvariant();
        var country = string.IsNullOrWhiteSpace(countryCode) ? null : countryCode.Trim();
        var category = string.IsNullOrWhiteSpace(categoryId) ? null : categoryId.Trim();
        if (nameQuery == null && country == null && category == null) return Channels;

        var result = new List<ChannelListing>();
        foreach (var channel in Channels)
        {
            if (country != null && !string.Equals(channel.CountryCode, country, StringComparison.OrdinalIgnoreCase))
                continue;
            if (category != null && !HasCategory(channel, category))
                continue;
            if (nameQuery != null && channel.SearchText.IndexOf(nameQuery, StringComparison.Ordinal) < 0)
                continue;
            result.Add(channel);
        }
        return result;
    }

    private static bool HasCategory(ChannelListing channel, string category)
    {
        foreach (var id in channel.CategoryIds)
        {
            if (string.Equals(id, category, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static List<T> Read<T>(Stream stream, string name)
    {
        if (stream.CanSeek) stream.Position = 0;
        try
        {
            return JsonSerializer.Deserialize<List<T>>(stream, Json) ?? new List<T>();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The iptv-org {name} file was not a JSON array.", ex);
        }
    }

    private static ChannelCatalog Build(
        List<ChannelRecord> channelRows,
        List<StreamRecord> streamRows,
        List<CountryRecord> countryRows,
        List<CategoryRecord> categoryRows)
    {
        var countryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var country in countryRows)
        {
            if (string.IsNullOrWhiteSpace(country.Code) || string.IsNullOrWhiteSpace(country.Name)) continue;
            countryNames.TryAdd(country.Code.Trim(), country.Name.Trim());
        }

        var categoryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var categories = new List<CategoryOption>();
        foreach (var category in categoryRows)
        {
            if (string.IsNullOrWhiteSpace(category.Id) || string.IsNullOrWhiteSpace(category.Name)) continue;
            var id = category.Id.Trim();
            if (!categoryNames.TryAdd(id, category.Name.Trim())) continue;
            categories.Add(new CategoryOption(id, category.Name.Trim()));
        }
        categories.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var streamsByChannel = new Dictionary<string, List<StreamRecord>>(StringComparer.OrdinalIgnoreCase);
        var orphans = new List<StreamRecord>();
        foreach (var stream in streamRows)
        {
            if (!StreamAddress.IsPlayable(stream.Url)) continue;
            var channelId = stream.Channel?.Trim();
            if (string.IsNullOrEmpty(channelId))
            {
                orphans.Add(stream);
                continue;
            }
            if (!streamsByChannel.TryGetValue(channelId, out var list))
                streamsByChannel[channelId] = list = new List<StreamRecord>();
            list.Add(stream);
        }

        var listings = new List<ChannelListing>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in channelRows)
        {
            if (string.IsNullOrWhiteSpace(channel.Id) || string.IsNullOrWhiteSpace(channel.Name)) continue;
            var id = channel.Id.Trim();
            if (!streamsByChannel.TryGetValue(id, out var list)) continue;
            if (!emitted.Add(id)) continue;
            var best = Best(list);
            usedUrls.Add(best.Url!.Trim());
            listings.Add(FromChannel(channel, best, countryNames, categoryNames));
        }

        foreach (var pair in streamsByChannel)
        {
            if (emitted.Contains(pair.Key)) continue;
            var best = Best(pair.Value);
            if (!usedUrls.Add(best.Url!.Trim())) continue;
            var name = string.IsNullOrWhiteSpace(best.Title) ? pair.Key.Trim() : best.Title.Trim();
            listings.Add(Loose(pair.Key.Trim(), name, best));
        }

        foreach (var orphan in orphans)
        {
            if (!usedUrls.Add(orphan.Url!.Trim())) continue;
            var name = string.IsNullOrWhiteSpace(orphan.Title) ? "Untitled stream" : orphan.Title.Trim();
            listings.Add(Loose("stream:" + orphan.Url!.Trim(), name, orphan));
        }

        listings.Sort(CompareListings);

        var countries = new List<CountryOption>(countryNames.Count);
        foreach (var pair in countryNames)
            countries.Add(new CountryOption(pair.Key, pair.Value));
        countries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return new ChannelCatalog(listings, countries, categories);
    }

    private static ChannelListing FromChannel(
        ChannelRecord channel,
        StreamRecord stream,
        Dictionary<string, string> countryNames,
        Dictionary<string, string> categoryNames)
    {
        var code = channel.Country?.Trim() ?? "";
        var countryName = code.Length == 0 ? "" : countryNames.TryGetValue(code, out var found) ? found : code;
        var ids = new List<string>();
        if (channel.Categories != null)
        {
            foreach (var raw in channel.Categories)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var id = raw.Trim();
                if (ids.Exists(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase))) continue;
                ids.Add(id);
            }
        }
        var categoryText = string.Join(", ", ids.Select(id => categoryNames.TryGetValue(id, out var name) ? name : id));
        return new ChannelListing(
            channel.Id!.Trim(),
            channel.Name!.Trim(),
            code,
            countryName,
            ids,
            categoryText,
            stream.Url!.Trim(),
            BlankToNull(stream.Quality),
            CleanAgent(stream.UserAgent),
            CleanReferrer(stream.Referrer),
            SearchBlob(channel.Name, channel.AltNames, stream.Title));
    }

    private static ChannelListing Loose(string id, string name, StreamRecord stream) =>
        new(id, name, "", "", Array.Empty<string>(), "", stream.Url!.Trim(), BlankToNull(stream.Quality),
            CleanAgent(stream.UserAgent), CleanReferrer(stream.Referrer), SearchBlob(name, null, stream.Title));

    private static string SearchBlob(string? name, List<string>? altNames, string? title)
    {
        var parts = new List<string>();
        Add(parts, name);
        if (altNames != null)
        {
            foreach (var alt in altNames) Add(parts, alt);
        }
        Add(parts, title);
        return string.Join('\n', parts);

        static void Add(List<string> target, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            target.Add(value.Trim().ToLowerInvariant());
        }
    }

    private static StreamRecord Best(List<StreamRecord> streams)
    {
        var best = streams[0];
        var bestScore = Score(best);
        for (var i = 1; i < streams.Count; i++)
        {
            var score = Score(streams[i]);
            if (score <= bestScore) continue;
            best = streams[i];
            bestScore = score;
        }
        return best;
    }

    private static int Score(StreamRecord stream)
    {
        var https = stream.Url!.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        return QualityRank(stream.Quality) * 2 + https;
    }

    /// <summary>Leading number in strings such as "1080p" or "576i". Missing or odd labels rank as 0.</summary>
    private static int QualityRank(string? quality)
    {
        if (string.IsNullOrEmpty(quality)) return 0;
        var value = 0;
        var any = false;
        foreach (var c in quality)
        {
            if (c is >= '0' and <= '9')
            {
                any = true;
                value = value * 10 + (c - '0');
                if (value > 100_000) return 100_000;
            }
            else if (any) break;
        }
        return any ? value : 0;
    }

    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? CleanAgent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        foreach (var c in trimmed)
        {
            if (c is '\r' or '\n' or '\0') return null;
        }
        return trimmed;
    }

    private static string? CleanReferrer(string? value) =>
        StreamAddress.TryGetPlayable(value, out var uri) ? uri.AbsoluteUri : null;

    private static int CompareListings(ChannelListing a, ChannelListing b)
    {
        var byCountry = string.Compare(a.CountryName, b.CountryName, StringComparison.OrdinalIgnoreCase);
        return byCountry != 0 ? byCountry : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }
}
