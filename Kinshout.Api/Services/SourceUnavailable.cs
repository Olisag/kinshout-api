namespace Kinshout.Api.Services;

/// <summary>
/// A source post is gone when the public page says so. An expired image link is not enough:
/// Facebook still has the photo inside a live post.
/// </summary>
public static class SourceUnavailable
{
    public static bool FacebookPluginSaysGone(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return false;

        return html.Contains("is no longer available", StringComparison.OrdinalIgnoreCase)
            || html.Contains("n’est plus disponible", StringComparison.Ordinal)
            || html.Contains("n'est plus disponible", StringComparison.Ordinal);
    }

    public static bool IsFacebookPostUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return false;

        if (parsed.Scheme is not ("https" or "http"))
            return false;

        var host = parsed.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        return host.Equals("facebook.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("m.facebook.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("fb.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("fb.watch", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryYouTubeWatchUrl(string? url, out string watchUrl)
    {
        watchUrl = "";
        var key = MediaCanonicalKey.FromUrl(url);
        if (key is null || !key.StartsWith("yt:", StringComparison.Ordinal))
            return false;

        var id = key["yt:".Length..];
        if (id.Length != 11)
            return false;

        watchUrl = "https://www.youtube.com/watch?v=" + id;
        return true;
    }
}
