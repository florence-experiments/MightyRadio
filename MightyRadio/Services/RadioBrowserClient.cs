using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using MightyRadio.Models;

namespace MightyRadio.Services;

/// <summary>
/// Minimal client for the free Radio Browser directory (https://www.radio-browser.info).
/// Follows the API guidance: discover mirrors (all.api.radio-browser.info), pick one at random,
/// fall back to the others, and send a descriptive User-Agent.
/// </summary>
public sealed class RadioBrowserClient
{
    private static readonly string[] FallbackServers =
    {
        "https://de1.api.radio-browser.info",
        "https://de2.api.radio-browser.info",
    };

    private readonly HttpClient _http = AppInfo.Http;
    private readonly SemaphoreSlim _discoverLock = new(1, 1);
    private string[]? _servers;
    private DateTime _discoveredUtc;
    private volatile int _preferred;
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    private sealed class ServerDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    /// <summary>Server list: /json/servers, then DNS (A/AAAA + reverse lookup), then the built-in fallback.</summary>
    private async Task<string[]> GetServersAsync(CancellationToken ct)
    {
        var cached = _servers;
        if (cached != null && DateTime.UtcNow - _discoveredUtc < TimeSpan.FromHours(6)) return cached;

        await _discoverLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_servers != null && DateTime.UtcNow - _discoveredUtc < TimeSpan.FromHours(6)) return _servers;

            var names = new List<string>();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                var list = await _http.GetFromJsonAsync<List<ServerDto>>("https://all.api.radio-browser.info/json/servers", Storage.JsonOptions, cts.Token).ConfigureAwait(false);
                if (list != null) names.AddRange(list.Select(s => s.Name));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Storage.Log(ex, "Radio Browser server list"); }

            if (names.Count == 0)
            {
                try
                {
                    var ips = await Dns.GetHostAddressesAsync("all.api.radio-browser.info", ct).ConfigureAwait(false);
                    foreach (var ip in ips.Where(i => i.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                    {
                        try
                        {
                            var entry = await Dns.GetHostEntryAsync(ip.ToString(), ct).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(entry.HostName)) names.Add(entry.HostName);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch { /* reverse lookup failed for this IP */ }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Storage.Log(ex, "Radio Browser DNS discovery"); }
            }

            var servers = names
                .Select(n => n.Trim().TrimEnd('.').ToLowerInvariant())
                .Where(n => n.EndsWith(".api.radio-browser.info", StringComparison.Ordinal) && Uri.CheckHostName(n) == UriHostNameType.Dns)
                .Distinct()
                .OrderBy(_ => Random.Shared.Next())
                .Select(n => "https://" + n)
                .ToList();
            foreach (var f in FallbackServers.OrderBy(_ => Random.Shared.Next()))
                if (!servers.Contains(f)) servers.Add(f);

            _servers = servers.ToArray();
            _discoveredUtc = DateTime.UtcNow;
            _preferred = 0;
            return _servers;
        }
        finally { _discoverLock.Release(); }
    }

    private async Task<T> GetAsync<T>(string pathAndQuery, CancellationToken ct)
    {
        var servers = await GetServersAsync(ct).ConfigureAwait(false);
        Exception? last = null;
        int start = _preferred;
        for (int i = 0; i < servers.Length; i++)
        {
            int idx = (start + i) % servers.Length;
            try
            {
                var result = await _http.GetFromJsonAsync<T>(servers[idx] + pathAndQuery, Storage.JsonOptions, ct).ConfigureAwait(false);
                if (result is null) throw new InvalidOperationException("Empty response");
                _preferred = idx;
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw new RadioBrowserException("Couldn't reach the station directory.", last);
    }

    public Task<List<Station>> SearchAsync(string? name, string? countryCode, string? tag, SortOption sort, int limit, CancellationToken ct)
    {
        var q = new StringBuilder("/json/stations/search?hidebroken=true");
        if (!string.IsNullOrWhiteSpace(name)) q.Append("&name=").Append(Uri.EscapeDataString(name.Trim()));
        if (!string.IsNullOrWhiteSpace(countryCode)) q.Append("&countrycode=").Append(Uri.EscapeDataString(countryCode.Trim()));
        if (!string.IsNullOrWhiteSpace(tag)) q.Append("&tag=").Append(Uri.EscapeDataString(tag.Trim().ToLowerInvariant()));
        q.Append("&order=").Append(Uri.EscapeDataString(sort.Order));
        q.Append("&reverse=").Append(sort.Reverse ? "true" : "false");
        q.Append("&limit=").Append(limit);
        return GetAsync<List<Station>>(q.ToString(), ct);
    }

    private sealed class CountryDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("iso_3166_1")] public string Code { get; set; } = "";
        [JsonPropertyName("stationcount")] public int StationCount { get; set; }
    }

    public async Task<List<CountryOption>> GetCountriesAsync(CancellationToken ct)
    {
        var list = await GetAsync<List<CountryDto>>("/json/countries?hidebroken=true&order=stationcount&reverse=true", ct).ConfigureAwait(false);
        return list
            .Where(c => !string.IsNullOrWhiteSpace(c.Code) && c.StationCount > 0)
            .GroupBy(c => c.Code.ToUpperInvariant())
            .Select(g => new CountryOption
            {
                Code = g.Key,
                Name = CountryNames.Get(g.Key, g.First().Name),
                StationCount = g.Sum(x => x.StationCount),
            })
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Tell Radio Browser a station was played (counts popularity). Once per station per session. UI thread only.</summary>
    public void ReportClick(string? stationUuid)
    {
        if (string.IsNullOrWhiteSpace(stationUuid) || stationUuid.Contains(':')) return; // only real Radio Browser ids
        if (!_reported.Add(stationUuid)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var servers = await GetServersAsync(cts.Token).ConfigureAwait(false);
                var server = servers[Math.Clamp(_preferred, 0, servers.Length - 1)];
                using var resp = await _http.GetAsync($"{server}/json/url/{Uri.EscapeDataString(stationUuid)}", cts.Token).ConfigureAwait(false);
            }
            catch { /* not important */ }
        });
    }
}

public sealed class RadioBrowserException : Exception
{
    public RadioBrowserException(string message, Exception? inner) : base(message, inner) { }
}
