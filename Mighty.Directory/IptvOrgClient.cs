using System.Net;
using System.Net.Http;

namespace Mighty.Directory;

/// <summary>Downloads the public iptv-org directory and builds a <see cref="ChannelCatalog"/>.</summary>
public sealed class IptvOrgClient : IDisposable
{
    public const string DefaultUserAgent = "MightyTV/0.1 (+https://github.com/florence-experiments/MightyRadio)";
    public const string ChannelsUrl = "https://iptv-org.github.io/api/channels.json";
    public const string StreamsUrl = "https://iptv-org.github.io/api/streams.json";
    public const string CountriesUrl = "https://iptv-org.github.io/api/countries.json";
    public const string CategoriesUrl = "https://iptv-org.github.io/api/categories.json";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public IptvOrgClient(HttpClient? http = null, string? userAgent = null)
    {
        if (http == null)
        {
            var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent.Trim());
            _ownsClient = true;
        }
        else
        {
            _http = http;
        }
    }

    public async Task<ChannelCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        var channelsTask = GetAsync(ChannelsUrl, cancellationToken);
        var streamsTask = GetAsync(StreamsUrl, cancellationToken);
        var countriesTask = GetAsync(CountriesUrl, cancellationToken);
        var categoriesTask = GetAsync(CategoriesUrl, cancellationToken);
        await Task.WhenAll(channelsTask, streamsTask, countriesTask, categoriesTask).ConfigureAwait(false);

        using var channels = new MemoryStream(await channelsTask.ConfigureAwait(false));
        using var streams = new MemoryStream(await streamsTask.ConfigureAwait(false));
        using var countries = new MemoryStream(await countriesTask.ConfigureAwait(false));
        using var categories = new MemoryStream(await categoriesTask.ConfigureAwait(false));
        return ChannelCatalog.Parse(channels, streams, countries, categories);
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

    private async Task<byte[]> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }
}
