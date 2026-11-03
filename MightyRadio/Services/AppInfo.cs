using System.Net;
using System.Net.Http;
using System.Reflection;

namespace MightyRadio.Services;

/// <summary>Version, identity and the shared HTTP client.</summary>
public static class AppInfo
{
    public const string RepoUrl = "https://github.com/florence-experiments/MightyRadio";

    public static string Version { get; } = ReadVersion();

    /// <summary>Descriptive User-Agent, as radio-browser.info asks for.</summary>
    public static string UserAgent => $"MightyRadio/{Version} (+{RepoUrl})";

    private static string ReadVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "1.1.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
    }

    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    });

    /// <summary>One HttpClient for the whole app (never disposed; lives as long as the process).</summary>
    public static HttpClient Http => SharedHttp.Value;
}

/// <summary>Only http/https URLs ever reach LibVLC or the browser.</summary>
public static class StreamUrl
{
    public static bool TryGetPlayable(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(u.Host)) return false;
        uri = u;
        return true;
    }

    public static bool IsPlayable(string? url) => TryGetPlayable(url, out _);

    /// <summary>URL without query string, for logs (query strings can hold listener tokens).</summary>
    public static string ForLog(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(none)";
        var i = url.IndexOf('?');
        return i < 0 ? url : url[..i] + "?…";
    }
}
