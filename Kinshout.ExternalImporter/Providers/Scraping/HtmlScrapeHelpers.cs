using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Kinshout.ExternalImporter.Providers.Scraping;

internal static partial class HtmlScrapeHelpers
{
    public static HtmlDocument Load(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }

    public static string? Text(HtmlNode? node) =>
        string.IsNullOrWhiteSpace(node?.InnerText)
            ? null
            : WebUtility.HtmlDecode(node.InnerText).Trim();

    public static string? Meta(HtmlDocument doc, string property) =>
        doc.DocumentNode.SelectSingleNode($"//meta[@property='{property}']")?.GetAttributeValue("content", string.Empty)?.Trim();

    public static string AbsoluteUrl(string baseUrl, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return baseUrl;

        if (Uri.TryCreate(relative, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            return relative;

        return new Uri(baseUri, relative).ToString();
    }

    public static string? ExtractIdFromPattern(string input, Regex pattern)
    {
        var match = pattern.Match(input);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static DateTime? ParseLooseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.ToUniversalTime();

        if (DateTime.TryParse(value, new CultureInfo("fr-FR"), DateTimeStyles.AssumeUniversal, out parsed))
            return parsed.ToUniversalTime();

        return null;
    }

    public static FeedPrice? ParsePrice(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = WebUtility.HtmlDecode(raw).Replace("\u00a0", " ").Trim();
        var formatted = Regex.Replace(text, @"\s+", " ");

        var currency = formatted.Contains('$') || formatted.Contains("USD", StringComparison.OrdinalIgnoreCase)
            ? "USD"
            : formatted.Contains("FC", StringComparison.OrdinalIgnoreCase) || formatted.Contains("CDF", StringComparison.OrdinalIgnoreCase)
                ? "CDF"
                : null;

        var digits = Regex.Replace(formatted, @"[^\d,.]", "");
        digits = digits.Replace(',', '.');
        var dotIndex = digits.IndexOf('.');
        if (dotIndex >= 0)
        {
            var after = digits[(dotIndex + 1)..].Replace(".", "");
            digits = digits[..dotIndex] + "." + after;
        }

        decimal? amount = decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

        if (amount is null && !Regex.IsMatch(formatted, @"\d"))
            return null;

        if (formatted.Length > 64)
            formatted = formatted[..64].TrimEnd();

        return new FeedPrice
        {
            Amount = amount,
            Currency = currency,
            Formatted = formatted,
            Negotiable = formatted.Contains("discut", StringComparison.OrdinalIgnoreCase)
                || formatted.Contains("nego", StringComparison.OrdinalIgnoreCase),
        };
    }

    public static string? ExtractPhone(string html)
    {
        var waMatch = WhatsAppRegex().Match(html);
        if (waMatch.Success)
            return waMatch.Groups[1].Value;

        var telMatch = TelRegex().Match(html);
        if (telMatch.Success)
            return telMatch.Groups[1].Value;

        return null;
    }

    public static List<string> ExtractImages(HtmlDocument doc, string baseUrl)
    {
        var images = doc.DocumentNode
            .SelectNodes("//img[@src]")
            ?.Select(node => node.GetAttributeValue("src", ""))
            .Where(src => !string.IsNullOrWhiteSpace(src))
            .Select(src => AbsoluteUrl(baseUrl, src))
            .Where(url => !url.Contains("logo", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return images ?? [];
    }

    public static JsonElement? TryParseJsonLd(string html)
    {
        var match = JsonLdRegex().Match(html);
        if (!match.Success)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    public static string? JsonString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static decimal? JsonDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    [GeneratedRegex(@"wa\.me/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WhatsAppRegex();

    [GeneratedRegex(@"tel:(\+?\d[\d\s.-]{7,})", RegexOptions.IgnoreCase)]
    private static partial Regex TelRegex();

    [GeneratedRegex(@"<script[^>]*type=""application/ld\+json""[^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex JsonLdRegex();
}
