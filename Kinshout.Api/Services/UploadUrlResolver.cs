namespace Kinshout.Api.Services;

public interface IUploadUrlResolver
{
    string? ToPublicUrl(string? url);
    string? ToStoragePath(string? url);
}

public sealed class UploadUrlResolver(
    Microsoft.Extensions.Options.IOptions<Kinshout.Api.Configuration.UploadStorageSettings> options,
    Microsoft.AspNetCore.Http.IHttpContextAccessor httpContextAccessor) : IUploadUrlResolver
{
    public string? ToPublicUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return trimmed;
        }

        var path = trimmed.StartsWith('/') ? trimmed : $"/{trimmed}";
        var baseUrl = GetBaseUrl();
        return string.IsNullOrEmpty(baseUrl) ? path : $"{baseUrl}{path}";
    }

    public string? ToStoragePath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.Trim();
        if (trimmed.StartsWith('/'))
            return trimmed;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            var path = absolute.AbsolutePath;
            if (path.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
                return path;
        }

        return trimmed;
    }

    private string GetBaseUrl()
    {
        var configured = options.Value.PublicBaseUrl?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(configured))
            return configured;

        var request = httpContextAccessor.HttpContext?.Request;
        if (request is null)
            return string.Empty;

        return $"{request.Scheme}://{request.Host.Value}".TrimEnd('/');
    }
}
