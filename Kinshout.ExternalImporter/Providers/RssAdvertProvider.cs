using System.Xml.Linq;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers;

public sealed class RssAdvertProvider(HttpClient http, ExternalProviderSettings settings) : IExternalAdvertProvider
{
    public string Name => settings.Name;

    public async Task<ProviderFetchResult> FetchAsync(CancellationToken ct)
    {
        var adverts = new List<SourceFeedAdvert>();
        await FetchEndpointAsync(settings.RecentUrl, adverts, ct);
        await FetchEndpointAsync(settings.PopularUrl, adverts, ct);

        var seenIds = adverts
            .Select(a => a.ExternalId ?? a.ExternalUrl)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var deduped = adverts
            .Where(a => !string.IsNullOrWhiteSpace(a.ExternalUrl) && !string.IsNullOrWhiteSpace(a.Title))
            .GroupBy(a => a.ExternalId ?? a.ExternalUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return ProviderFetchResult.From(deduped, seenIds);
    }

    private async Task FetchEndpointAsync(string? url, List<SourceFeedAdvert> adverts, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        using var response = await http.SendAsync(ProviderHttp.CreateRequest(url, settings), ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
        adverts.AddRange(ParseRss(document));
    }

    private static IEnumerable<SourceFeedAdvert> ParseRss(XDocument document)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var rssItems = document.Descendants("item").Select(item => new SourceFeedAdvert
        {
            ExternalId = Value(item, "guid") ?? Value(item, "link"),
            ExternalUrl = Value(item, "link"),
            Title = Value(item, "title"),
            Description = Value(item, "description"),
            Summary = Value(item, "description"),
            PublishedAt = ParseDate(Value(item, "pubDate")),
        });

        var atomItems = document.Descendants(atom + "entry").Select(entry =>
        {
            var link = entry.Elements(atom + "link")
                .FirstOrDefault(e => (string?)e.Attribute("rel") is null or "alternate")
                ?.Attribute("href")
                ?.Value;

            return new SourceFeedAdvert
            {
                ExternalId = entry.Element(atom + "id")?.Value ?? link,
                ExternalUrl = link,
                Title = entry.Element(atom + "title")?.Value,
                Description = entry.Element(atom + "summary")?.Value ?? entry.Element(atom + "content")?.Value,
                Summary = entry.Element(atom + "summary")?.Value,
                PublishedAt = ParseDate(entry.Element(atom + "published")?.Value ?? entry.Element(atom + "updated")?.Value),
            };
        });

        return rssItems.Concat(atomItems);
    }

    private static string? Value(XElement element, string name) =>
        element.Element(name)?.Value.Trim();

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;
}
