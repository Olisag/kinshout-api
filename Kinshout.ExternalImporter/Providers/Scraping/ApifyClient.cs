using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers.Scraping;

internal sealed class ApifyClient(HttpClient http, ExternalProviderSettings settings)
{
    private const string BaseUrl = "https://api.apify.com/v2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<JsonDocument> RunActorAndGetDatasetAsync(object input, CancellationToken ct)
    {
        var actorId = EncodeActorId(ResolveActorId());
        var token = ResolveToken();

        using var startResponse = await SendAsync(
            HttpMethod.Post,
            $"/acts/{actorId}/runs",
            input,
            token,
            ct);

        var startBody = await startResponse.Content.ReadAsStringAsync(ct);
        if (!startResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Apify run start failed ({(int)startResponse.StatusCode}): {Trim(startBody, 400)}");

        using var startDoc = JsonDocument.Parse(startBody);
        var run = startDoc.RootElement.GetProperty("data");
        var runId = run.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Apify run response missing id.");
        var datasetId = run.GetProperty("defaultDatasetId").GetString()
            ?? throw new InvalidOperationException("Apify run response missing defaultDatasetId.");

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, settings.ActorTimeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            using var statusResponse = await SendAsync(
                HttpMethod.Get,
                $"/actor-runs/{runId}?waitForFinish=30",
                body: null,
                token,
                ct);

            var statusBody = await statusResponse.Content.ReadAsStringAsync(ct);
            if (!statusResponse.IsSuccessStatusCode)
                throw new InvalidOperationException($"Apify run status failed ({(int)statusResponse.StatusCode}): {Trim(statusBody, 400)}");

            using var statusDoc = JsonDocument.Parse(statusBody);
            var status = statusDoc.RootElement.GetProperty("data").GetProperty("status").GetString();
            if (status is "SUCCEEDED")
                break;

            if (status is "FAILED" or "ABORTED" or "TIMED-OUT")
            {
                throw new InvalidOperationException($"Apify actor run {runId} ended with status {status}.");
            }
        }

        if (DateTime.UtcNow >= deadline)
            throw new TimeoutException($"Apify actor run {runId} did not finish within {settings.ActorTimeoutSeconds}s.");

        using var datasetResponse = await SendAsync(
            HttpMethod.Get,
            $"/datasets/{datasetId}/items?format=json&clean=true",
            body: null,
            token,
            ct);

        var datasetBody = await datasetResponse.Content.ReadAsStringAsync(ct);
        if (!datasetResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Apify dataset fetch failed ({(int)datasetResponse.StatusCode}): {Trim(datasetBody, 400)}");

        return JsonDocument.Parse(datasetBody);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string token,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, $"{BaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
        }

        return await http.SendAsync(request, ct);
    }

    private string ResolveActorId() =>
        string.IsNullOrWhiteSpace(settings.ApifyActorId)
            ? "apify/facebook-marketplace-scraper"
            : settings.ApifyActorId.Trim();

    private string ResolveToken()
    {
        string? token = null;
        if (!string.IsNullOrWhiteSpace(settings.ApifyToken))
            token = settings.ApifyToken;
        else if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            token = settings.ApiKey;
        else if (!string.IsNullOrWhiteSpace(settings.AccessToken))
            token = settings.AccessToken;

        token = token?.Trim().Trim('\r', '\n');
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "Apify token required. Set providers[].apifyToken, apiKey, or ${APIFY_TOKEN}.");

        return token;
    }

    internal static string EncodeActorId(string actorId) =>
        actorId.Replace('/', '~');

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
