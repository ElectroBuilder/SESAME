namespace VisualSSH.Services;

public static class StoreUrls
{
    public static bool IsPlaceholder(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        var n = url.ToLowerInvariant();
        return n.Contains("/_.gif") || n.Contains("/_.png") || n.EndsWith("_.gif") ||
               n.Contains("defaults/avatar") || n.Contains("placeholder");
    }

    public static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (url.StartsWith("//", StringComparison.Ordinal))
            url = "https:" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return null;
        return uri.AbsoluteUri;
    }
}
