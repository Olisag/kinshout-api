using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers;

internal static class ProviderHttp
{
    public static HttpRequestMessage CreateRequest(string url, ExternalProviderSettings settings)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("KinshoutExternalImporter/1.0");

        foreach (var (name, value) in settings.Headers)
        {
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
                request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }
}
