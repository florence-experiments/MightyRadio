using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using MightyRadio.Models;

namespace MightyRadio.Services;

/// <summary>
/// Minimal client for the free Radio Browser directory (https://www.radio-browser.info).
/// Only talks to the Radio Browser API servers; no other network calls.
/// </summary>
public sealed class RadioBrowserClient : IDisposable
{
    private static readonly string[] Servers =
    {
        "https://de1.api.radio-browser.info",
        "https://de2.api.radio-browser.info",
    };

    private readonly HttpClient _http;
    private int _preferred;

    public RadioBrowserClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MightyRadio/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    private async Task<T> GetAsync<T>(string pathAndQuery, CancellationToken ct)
    {
        Exception? last = null;
        for (int i = 0; i < Servers.Length; i++)
        {
            int idx = (_preferred + i) % Servers.Length;
            try
            {
                var result = await _http.GetFromJsonAsync<T>(Servers[idx] + pathAndQuery, Storage.JsonOptions, ct).ConfigureAwait(false);
                _preferred = idx;
                if (result is null) throw new InvalidOperationException("Empty response");
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
        [System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("iso_3166_1")] public string Code { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("stationcount")] public int StationCount { get; set; }
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

    /// <summary>Politely tell Radio Browser a station was played (counts popularity). Fire-and-forget.</summary>
    public void ReportClick(string? stationUuid)
    {
        if (string.IsNullOrWhiteSpace(stationUuid) || stationUuid.Contains(':')) return; // only real Radio Browser ids
        var server = Servers[_preferred];
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var resp = await _http.GetAsync($"{server}/json/url/{Uri.EscapeDataString(stationUuid)}", cts.Token).ConfigureAwait(false);
            }
            catch { /* not important */ }
        });
    }

    public void Dispose() => _http.Dispose();
}

public sealed class RadioBrowserException : Exception
{
    public RadioBrowserException(string message, Exception? inner) : base(message, inner) { }
}
