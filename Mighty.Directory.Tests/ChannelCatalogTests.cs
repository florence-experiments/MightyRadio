using System.Text;
using Xunit;

namespace Mighty.Directory.Tests;

public class ChannelCatalogTests
{
    private const string Channels = """
        [
          {"id":"BBCOne.uk","name":"BBC One","alt_names":["BBC 1"],"country":"UK","categories":["general","news"],"is_nsfw":false,"closed":null},
          {"id":"Adult.xx","name":"Night","country":"US","categories":["xxx"],"is_nsfw":true},
          {"id":"NoStream.uk","name":"Silent","country":"UK","categories":["general"]},
          {"id":"Old.uk","name":"Old Channel","country":"UK","categories":["classic"],"closed":"2001-01-01"},
          {"id":"Rtmp.uk","name":"Raw Socket","country":"UK","categories":["general"]}
        ]
        """;

    private const string Streams = """
        [
          {"channel":"BBCOne.uk","url":"http://low.example/bbc","quality":"480p","labels":["Not 24/7"]},
          {"channel":"BBCOne.uk","title":"BBC One HD","url":"https://hi.example/bbc.m3u8","quality":"1080p","user_agent":"TestAgent","referrer":"https://www.bbc.co.uk/"},
          {"channel":"BBCOne.uk","url":"rtmp://bad.example/bbc","quality":"2160p"},
          {"channel":"Adult.xx","url":"https://adult.example/stream","quality":"720p"},
          {"channel":"Old.uk","url":"https://old.example/stream","quality":"576i","referrer":"about:blank","user_agent":"bad\nagent"},
          {"channel":"Rtmp.uk","url":"rtmp://only.example/live"},
          {"channel":null,"title":"Orphan News","url":"https://orphan.example/a.m3u8","quality":"360p","labels":[]},
          {"channel":"Missing.id","title":"Ghost","url":"https://ghost.example/a","quality":"720p"}
        ]
        """;

    private const string Countries = """
        [
          {"name":"United Kingdom","code":"UK","languages":["eng"],"flag":"🇬🇧"},
          {"name":"United States","code":"US"},
          {"name":"France","code":"FR"}
        ]
        """;

    private const string Categories = """
        [
          {"id":"general","name":"General","description":"Mixed"},
          {"id":"news","name":"News"},
          {"id":"xxx","name":"XXX"},
          {"id":"classic","name":"Classic"}
        ]
        """;

    [Fact]
    public void Parse_joins_directory_and_keeps_every_country_and_category()
    {
        var catalog = ParseSample();

        Assert.Equal(new[] { "Ghost", "Orphan News", "BBC One", "Old Channel", "Night" }, catalog.Channels.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { "FR", "UK", "US" }, catalog.Countries.Select(c => c.Code).ToArray());
        Assert.Equal(new[] { "classic", "general", "news", "xxx" }, catalog.Categories.Select(c => c.Id).ToArray());
        Assert.DoesNotContain(catalog.Channels, c => c.Name is "Silent" or "Raw Socket");

        var bbc = catalog.Channels.Single(c => c.Id == "BBCOne.uk");
        Assert.Equal("https://hi.example/bbc.m3u8", bbc.StreamUrl);
        Assert.Equal("1080p", bbc.Quality);
        Assert.Equal("TestAgent", bbc.UserAgent);
        Assert.Equal("https://www.bbc.co.uk/", bbc.Referrer);
        Assert.Equal("United Kingdom · General, News · 1080p", bbc.Details);

        var old = catalog.Channels.Single(c => c.Id == "Old.uk");
        Assert.Null(old.UserAgent);
        Assert.Null(old.Referrer);
        Assert.Contains(catalog.Channels, c => c.Name == "Night");
    }

    [Fact]
    public void Filter_matches_name_country_and_category()
    {
        var catalog = ParseSample();

        Assert.Equal("BBC One", Assert.Single(catalog.Filter("bbc 1", null, null)).Name);
        Assert.Equal("BBC One", Assert.Single(catalog.Filter("BBC", "uk", "news")).Name);
        Assert.Empty(catalog.Filter("bbc", "US", null));
        Assert.Empty(catalog.Filter(null, "FR", null));

        var ukGeneral = catalog.Filter(null, "UK", "general").Select(c => c.Name).ToArray();
        Assert.Equal(new[] { "BBC One" }, ukGeneral);

        Assert.Equal("Night", Assert.Single(catalog.Filter(null, null, "xxx")).Name);
        Assert.Equal(catalog.Channels.Count, catalog.Filter("  ", " ", null).Count);
    }

    [Fact]
    public void Bad_json_is_a_clear_error()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ChannelCatalog.Parse(Json("{"), Json("[]"), Json("[]"), Json("[]")));
        Assert.Contains("channels", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Library_does_not_reference_wpf()
    {
        var names = typeof(ChannelCatalog).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("PresentationFramework", names);
        Assert.DoesNotContain("PresentationCore", names);
        Assert.DoesNotContain("WindowsBase", names);
        Assert.DoesNotContain("Microsoft.Maui", names);
    }

    /// <summary>
    /// Hits the public iptv-org files. Set MIGHTY_IPTV_LIVE=1 to run it; everyday test runs skip the network.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task Live_iptv_org_directory_parses()
    {
        if (Environment.GetEnvironmentVariable("MIGHTY_IPTV_LIVE") != "1") return;

        using var client = new IptvOrgClient();
        var catalog = await client.LoadAsync();

        Assert.True(catalog.Channels.Count > 1000, $"expected thousands of playable rows, got {catalog.Channels.Count}");
        Assert.Contains(catalog.Countries, c => c.Code == "UK" && c.Name == "United Kingdom");
        Assert.Contains(catalog.Categories, c => c.Id == "general");
        Assert.NotEmpty(catalog.Filter(null, "UK", null));
        Assert.NotEmpty(catalog.Filter("bbc", ChannelCatalog.UkCountryCode, null));
        Assert.All(catalog.Channels, channel =>
        {
            Assert.True(StreamAddress.IsPlayable(channel.StreamUrl), channel.StreamUrl);
            Assert.False(string.IsNullOrWhiteSpace(channel.Name));
        });
    }

    private static ChannelCatalog ParseSample() =>
        ChannelCatalog.Parse(Json(Channels), Json(Streams), Json(Countries), Json(Categories));

    private static Stream Json(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));
}
