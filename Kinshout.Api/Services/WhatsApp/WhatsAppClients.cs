using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services.WhatsApp;

public interface IOpenMeteoForecastClient
{
    Task<WeatherDay?> GetKinshasaAsync(DateTime localDate, CancellationToken ct);
}

public sealed record TrafficMeasurement(string? RunId, IReadOnlyList<TrafficObservation> Observations);

public interface IApifyTrafficClient
{
    Task<TrafficMeasurement> MeasureAsync(DateTime observedAtUtc, CancellationToken ct);
}

public sealed record ChannelPublishResult(bool Accepted, string? MessageId, bool Unsupported, bool Ambiguous, bool Retryable, string? Error);

public interface IWhatsAppChannelPublisher
{
    Task<ChannelPublishResult> SendTextAsync(string destination, string body, CancellationToken ct);
    Task<ChannelPublishResult> SendPollAsync(string destination, string question, IReadOnlyList<string> options, CancellationToken ct);
    Task<bool> MessageExistsAsync(string destination, string messageId, CancellationToken ct);
}

public sealed class OpenMeteoForecastClient(IHttpClientFactory http, IOptions<WhatsAppPublishingSettings> settings) : IOpenMeteoForecastClient
{
    public const string HttpClientName = "whatsapp-open-meteo";

    public async Task<WeatherDay?> GetKinshasaAsync(DateTime localDate, CancellationToken ct)
    {
        var config = settings.Value;
        var url = $"{config.OpenMeteoBaseUrl.TrimEnd('/')}?latitude={config.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={config.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}&timezone=Africa%2FKinshasa&forecast_days=2&daily=temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&hourly=temperature_2m,apparent_temperature,precipitation_probability,precipitation,weather_code,wind_speed_10m";
        var client = http.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return OpenMeteoReader.Read(document.RootElement, localDate.Date);
    }
}

public static class OpenMeteoReader
{
    public static WeatherDay? Read(JsonElement root, DateTime localDate)
    {
        if (!root.TryGetProperty("hourly", out var hourly))
            return null;
        var times = Strings(hourly, "time");
        var temps = Doubles(hourly, "temperature_2m");
        var apparent = Doubles(hourly, "apparent_temperature");
        var pop = Doubles(hourly, "precipitation_probability");
        var precip = Doubles(hourly, "precipitation");
        var codes = Doubles(hourly, "weather_code");
        var wind = Doubles(hourly, "wind_speed_10m");
        var hours = new List<WeatherHour>();
        for (var i = 0; i < times.Count; i++)
        {
            if (!DateTime.TryParse(times[i], out var local) || local.Date != localDate.Date)
                continue;
            hours.Add(new WeatherHour(
                local,
                At(temps, i),
                At(apparent, i),
                (int)Math.Round(At(pop, i)),
                At(precip, i),
                (int)At(codes, i),
                At(wind, i)));
        }
        if (hours.Count < 12)
            return null;
        var daily = Daily(root, localDate);
        return new WeatherDay(
            localDate.Date,
            daily.Min ?? hours.Min(h => h.Temperature),
            daily.Max ?? hours.Max(h => h.Temperature),
            daily.ApparentMin ?? hours.Min(h => h.Apparent),
            daily.ApparentMax ?? hours.Max(h => h.Apparent),
            hours);
    }

    private static (double? Min, double? Max, double? ApparentMin, double? ApparentMax) Daily(JsonElement root, DateTime localDate)
    {
        if (!root.TryGetProperty("daily", out var daily))
            return (null, null, null, null);
        var times = Strings(daily, "time");
        var index = times.FindIndex(t => DateTime.TryParse(t, out var day) && day.Date == localDate.Date);
        if (index < 0)
            return (null, null, null, null);
        var min = Doubles(daily, "temperature_2m_min");
        var max = Doubles(daily, "temperature_2m_max");
        var apparentMin = Doubles(daily, "apparent_temperature_min");
        var apparentMax = Doubles(daily, "apparent_temperature_max");
        return (AtNullable(min, index), AtNullable(max, index), AtNullable(apparentMin, index), AtNullable(apparentMax, index));
    }

    private static List<string> Strings(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var array))
            return [];
        return array.EnumerateArray().Select(item => item.GetString() ?? "").ToList();
    }

    private static List<double> Doubles(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var array))
            return [];
        return array.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Number ? item.GetDouble() : 0).ToList();
    }

    private static double At(List<double> values, int index) => index < values.Count ? values[index] : 0;

    private static double? AtNullable(List<double> values, int index) => index < values.Count ? values[index] : null;
}

public sealed class ApifyTrafficClient(IHttpClientFactory http, IOptions<WhatsAppPublishingSettings> settings) : IApifyTrafficClient
{
    public const string HttpClientName = "whatsapp-apify";

    public async Task<TrafficMeasurement> MeasureAsync(DateTime observedAtUtc, CancellationToken ct)
    {
        var config = settings.Value;
        if (string.IsNullOrWhiteSpace(config.ApifyToken))
            return new TrafficMeasurement(null, []);
        var routes = TrafficRouteCatalogue.Routes.Where(r => r.Enabled).ToList();
        var payload = JsonSerializer.Serialize(new
        {
            routes = routes.Select(r => new { label = r.Id, origin = r.Origin, destination = r.Destination, travelMode = "driving" }),
            travelModes = new[] { "driving" },
            timeMode = "now",
            maxRoutes = 1,
            outputFormat = "request",
            includeSteps = false,
            includePolyline = false,
            language = "fr",
            countryCode = "CD",
            fixAmbiguousPlaces = true,
            maxResults = routes.Count,
            maxConcurrency = 5,
        });
        var client = http.CreateClient(HttpClientName);
        using var start = new HttpRequestMessage(HttpMethod.Post, $"https://api.apify.com/v2/acts/{config.ApifyActorId}/runs");
        start.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApifyToken.Trim());
        start.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var started = await client.SendAsync(start, ct);
        if (!started.IsSuccessStatusCode)
            throw new HttpRequestException($"Apify start returned {(int)started.StatusCode}.");
        using var startedJson = JsonDocument.Parse(await started.Content.ReadAsStringAsync(ct));
        var runId = startedJson.RootElement.GetProperty("data").GetProperty("id").GetString();
        var datasetId = startedJson.RootElement.GetProperty("data").GetProperty("defaultDatasetId").GetString();
        string status = "READY";
        for (var attempt = 0; attempt < 16; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            using var poll = new HttpRequestMessage(HttpMethod.Get, $"https://api.apify.com/v2/actor-runs/{runId}");
            poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApifyToken.Trim());
            using var polled = await client.SendAsync(poll, ct);
            using var pollJson = JsonDocument.Parse(await polled.Content.ReadAsStringAsync(ct));
            status = pollJson.RootElement.GetProperty("data").GetProperty("status").GetString() ?? status;
            if (status is "SUCCEEDED" or "FAILED" or "ABORTED" or "TIMED-OUT")
                break;
        }
        if (status != "SUCCEEDED")
            throw new HttpRequestException($"Apify run ended {status}.");
        using var itemsRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.apify.com/v2/datasets/{datasetId}/items?format=json&clean=true");
        itemsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApifyToken.Trim());
        using var itemsResponse = await client.SendAsync(itemsRequest, ct);
        using var itemsJson = JsonDocument.Parse(await itemsResponse.Content.ReadAsStringAsync(ct));
        return new TrafficMeasurement(runId, ApifyTrafficReader.Read(itemsJson.RootElement, observedAtUtc));
    }
}

public static class ApifyTrafficReader
{
    public static IReadOnlyList<TrafficObservation> Read(JsonElement items, DateTime observedAtUtc)
    {
        var list = new List<TrafficObservation>();
        foreach (var item in items.EnumerateArray())
        {
            var label = item.TryGetProperty("label", out var labelValue) ? labelValue.GetString() : null;
            var route = TrafficRouteCatalogue.Routes.FirstOrDefault(r => r.Id == label && r.Enabled);
            if (route is null)
                continue;
            var via = Via(item);
            if (!TrafficRouteCatalogue.ViaMatches(route, via))
                continue;
            int? current = Seconds(item, "durationInTrafficSeconds");
            int? reference = Seconds(item, "durationWithoutTrafficSeconds");
            var level = item.TryGetProperty("trafficLevel", out var levelValue) ? levelValue.GetString() : null;
            if (current is null)
                continue;
            list.Add(new TrafficObservation(route.Id, route.RoadName, route.Segment, current, reference, level, false, observedAtUtc, via));
        }
        return list;
    }

    private static string? Via(JsonElement item)
    {
        if (item.TryGetProperty("via", out var via) && via.ValueKind == JsonValueKind.String)
            return via.GetString();
        if (item.TryGetProperty("routes", out var routes) && routes.ValueKind == JsonValueKind.Array)
        {
            var first = routes.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("summary", out var summary))
                return summary.GetString();
        }
        return null;
    }

    private static int? Seconds(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.GetInt32();
    }
}

public sealed class WapitoChannelPublisher(IHttpClientFactory http, IOptions<WhatsAppPublishingSettings> settings) : IWhatsAppChannelPublisher
{
    public const string HttpClientName = "whatsapp-wapito";
    private bool _pollsUnsupported;

    public Task<ChannelPublishResult> SendTextAsync(string destination, string body, CancellationToken ct) =>
        PostAsync("/messages/text", new { to = destination, body, typing_time = 0 }, ct);

    public async Task<ChannelPublishResult> SendPollAsync(string destination, string question, IReadOnlyList<string> options, CancellationToken ct)
    {
        if (_pollsUnsupported)
            return new ChannelPublishResult(false, null, true, false, false, "polls-unsupported");
        var result = await PostAsync("/messages/poll", new { to = destination, body = question, options, typing_time = 0 }, ct);
        if (result.Unsupported)
            _pollsUnsupported = true;
        return result;
    }

    public async Task<bool> MessageExistsAsync(string destination, string messageId, CancellationToken ct)
    {
        var config = settings.Value;
        if (string.IsNullOrWhiteSpace(config.WapitoToken))
            return false;
        var client = http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{config.WapitoBaseUrl.TrimEnd('/')}/newsletters/{Uri.EscapeDataString(destination)}/messages?limit=20");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.WapitoToken.Trim());
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return false;
        var json = await response.Content.ReadAsStringAsync(ct);
        return json.Contains(MessageSuffix(messageId), StringComparison.Ordinal);
    }

    private async Task<ChannelPublishResult> PostAsync(string path, object payload, CancellationToken ct)
    {
        var config = settings.Value;
        if (string.IsNullOrWhiteSpace(config.WapitoToken))
            return new ChannelPublishResult(false, null, false, false, false, "missing-token");
        var client = http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, config.WapitoBaseUrl.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.WapitoToken.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ChannelPublishResult(false, null, false, true, false, "timeout");
        }
        var text = await response.Content.ReadAsStringAsync(ct);
        if ((int)response.StatusCode == 429)
            return new ChannelPublishResult(false, null, false, false, true, "rate-limit");
        if ((int)response.StatusCode is 404 or 405 or 422)
            return new ChannelPublishResult(false, null, true, false, false, "unsupported");
        if ((int)response.StatusCode is 401 or 403)
            return new ChannelPublishResult(false, null, false, false, false, "authentication");
        if (!response.IsSuccessStatusCode)
            return new ChannelPublishResult(false, null, false, false, (int)response.StatusCode >= 500, $"http-{(int)response.StatusCode}");
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("id", out var idValue))
                id = idValue.GetString();
        }
        catch (JsonException)
        {
            return new ChannelPublishResult(true, null, false, true, false, "unreadable-response");
        }
        return new ChannelPublishResult(true, id, false, id is null, false, null);
    }

    private static string MessageSuffix(string messageId)
    {
        var marker = messageId.LastIndexOf('_');
        return marker >= 0 ? messageId[(marker + 1)..] : messageId;
    }
}
