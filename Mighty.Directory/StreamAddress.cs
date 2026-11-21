namespace Mighty.Directory;

/// <summary>
/// Only http/https addresses are treated as playable. Mighty Radio has the same rule in its own
/// copy; a later merged app can share this one.
/// </summary>
public static class StreamAddress
{
    public static bool TryGetPlayable(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(parsed.Host)) return false;
        uri = parsed;
        return true;
    }

    public static bool IsPlayable(string? url) => TryGetPlayable(url, out _);
}
