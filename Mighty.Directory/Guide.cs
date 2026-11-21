using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Mighty.Directory;

/// <summary>One iptv-org guide that actually has an http(s) programme file.</summary>
public sealed class GuideLink
{
    public GuideLink(string siteId, string url)
    {
        SiteId = siteId;
        Url = url;
    }

    public string SiteId { get; }
    public string Url { get; }
}

/// <summary>channel id → best guide link from guides.json. Channels with no source are absent.</summary>
public sealed class GuideDirectory
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, GuideLink> _links;

    private GuideDirectory(Dictionary<string, GuideLink> links) => _links = links;

    public GuideLink? Find(string? channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId)) return null;
        return _links.TryGetValue(channelId.Trim(), out var link) ? link : null;
    }

    public static GuideDirectory Parse(Stream json)
    {
        if (json.CanSeek) json.Position = 0;
        List<GuideRecord> rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<GuideRecord>>(json, Json) ?? new List<GuideRecord>();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The iptv-org guides file was not a JSON array.", ex);
        }

        var bestScore = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var links = new Dictionary<string, GuideLink>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Channel) || string.IsNullOrWhiteSpace(row.SiteId)) continue;
            if (row.SiteId.Trim() == "#") continue;
            var url = FirstPlayable(row.Sources);
            if (url == null) continue;
            var score = 1;
            if (string.Equals(row.Lang, "en", StringComparison.OrdinalIgnoreCase)) score += 2;
            if (string.IsNullOrWhiteSpace(row.Feed)) score += 1;
            var id = row.Channel.Trim();
            if (bestScore.TryGetValue(id, out var previous) && previous >= score) continue;
            bestScore[id] = score;
            links[id] = new GuideLink(row.SiteId.Trim(), url);
        }
        return new GuideDirectory(links);
    }

    private static string? FirstPlayable(List<GuideSourceRecord>? sources)
    {
        if (sources == null) return null;
        foreach (var source in sources)
        {
            if (StreamAddress.TryGetPlayable(source.Url, out var uri)) return uri.AbsoluteUri;
        }
        return null;
    }

    private sealed class GuideRecord
    {
        [JsonPropertyName("channel")] public string? Channel { get; set; }
        [JsonPropertyName("feed")] public string? Feed { get; set; }
        [JsonPropertyName("site_id")] public string? SiteId { get; set; }
        [JsonPropertyName("lang")] public string? Lang { get; set; }
        [JsonPropertyName("sources")] public List<GuideSourceRecord>? Sources { get; set; }
    }

    private sealed class GuideSourceRecord
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}

/// <summary>Reads an XMLTV file and names the programme on now and the one after it.</summary>
public static class XmlTvGuide
{
    public static string? NowNext(Stream xml, string? siteId, DateTimeOffset moment)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(xml);
        }
        catch
        {
            return null;
        }

        var rows = new List<(DateTimeOffset Start, DateTimeOffset Stop, string Title)>();
        var matched = new List<(DateTimeOffset Start, DateTimeOffset Stop, string Title)>();
        foreach (var node in doc.Descendants().Where(e => e.Name.LocalName == "programme"))
        {
            var title = node.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value?.Trim();
            if (string.IsNullOrEmpty(title)) continue;
            if (!TryTime(node.Attribute("start")?.Value, out var start)) continue;
            if (!TryTime(node.Attribute("stop")?.Value, out var stop) || stop <= start) continue;
            var row = (start, stop, title);
            rows.Add(row);
            var channel = node.Attribute("channel")?.Value;
            if (!string.IsNullOrEmpty(siteId) && string.Equals(channel, siteId, StringComparison.OrdinalIgnoreCase))
                matched.Add(row);
        }

        var list = matched.Count > 0 ? matched : rows;
        string? now = null;
        var nowStop = moment;
        foreach (var row in list)
        {
            if (row.Start <= moment && moment < row.Stop)
            {
                now = row.Title;
                nowStop = row.Stop;
            }
        }

        string? next = null;
        var nextStart = DateTimeOffset.MaxValue;
        foreach (var row in list)
        {
            if (row.Start < nowStop || row.Start >= nextStart) continue;
            if (now != null && row.Title == now && row.Start <= moment) continue;
            next = row.Title;
            nextStart = row.Start;
        }

        if (now == null && next == null) return null;
        if (now != null && next != null) return $"Now: {now}  ·  Next: {next}";
        return now != null ? $"Now: {now}" : $"Next: {next}";
    }

    private static bool TryTime(string? text, out DateTimeOffset time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length < 14) return false;
        if (!DateTime.TryParseExact(text.AsSpan(0, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
            return false;
        var offset = TimeSpan.Zero;
        var zone = text.Length > 15 ? text[15..].Trim() : "";
        if (zone.Length >= 5 && (zone[0] == '+' || zone[0] == '-')
            && int.TryParse(zone.AsSpan(1, 2), out var hours)
            && int.TryParse(zone.AsSpan(3, 2), out var minutes))
        {
            var sign = zone[0] == '-' ? -1 : 1;
            offset = TimeSpan.FromMinutes(sign * (hours * 60 + minutes));
        }
        time = new DateTimeOffset(wall, offset);
        return true;
    }
}
