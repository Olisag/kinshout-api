using Kinshout.ExternalImporter.Configuration;
using Kinshout.ExternalImporter.Providers.Scraping;

namespace Kinshout.Api.Tests;

public class FeedDiscoveryPlannerTests
{
    private static readonly DateTime MondayKinshasa = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<CatalogueSubject> Names(int count) =>
        Enumerable.Range(1, count)
            .Select(index => new CatalogueSubject($"Person {index}", $"\"Person {index}\"", "influence", [$"person {index}"]))
            .ToList();

    [Fact]
    public void TikTokKeepsFourSearchesAndIncludesCatalogueNames()
    {
        var queries = FeedDiscoveryPlanner.SelectForPlatform("tiktok", MondayKinshasa, 4, Names(100));

        Assert.Equal(4, queries.Count);
        Assert.Contains(queries, query => query.Lane == FeedDiscoveryPlanner.Lane.Buzz && query.Text.Contains("insolite", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, query => query.Text == "weekend kinshasa");
        Assert.Equal(2, queries.Count(query => query.Lane == FeedDiscoveryPlanner.Lane.Catalogue));
        Assert.Equal(4, queries.Select(query => query.Text).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void MondayThemeFollowsKinshasaNotUtc()
    {
        var stillSundayInKinshasa = new DateTime(2026, 11, 22, 20, 0, 0, DateTimeKind.Utc);

        var queries = FeedDiscoveryPlanner.SelectForPlatform("tiktok", stillSundayInKinshasa, 4, Names(10));

        Assert.Contains(queries, query => query.Text == "culture congolaise");
        Assert.DoesNotContain(queries, query => query.Text == "weekend kinshasa");
    }

    [Fact]
    public void NextDayContinuesThroughTheCatalogue()
    {
        var first = FeedDiscoveryPlanner.SelectForPlatform("tiktok", MondayKinshasa, 4, Names(100))
            .Where(query => query.Lane == FeedDiscoveryPlanner.Lane.Catalogue)
            .Select(query => query.Text)
            .ToList();
        var second = FeedDiscoveryPlanner.SelectForPlatform("tiktok", MondayKinshasa.AddDays(1), 4, Names(100))
            .Where(query => query.Lane == FeedDiscoveryPlanner.Lane.Catalogue)
            .Select(query => query.Text)
            .ToList();

        Assert.Equal(2, first.Count);
        Assert.Empty(first.Intersect(second, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void FacebookCoversThirtyNamesWithoutRepeatingThem()
    {
        var queries = FeedDiscoveryPlanner.SelectForPlatform("facebook", MondayKinshasa, 4, Names(60))
            .Where(query => query.Lane == FeedDiscoveryPlanner.Lane.Catalogue)
            .ToList();

        Assert.Equal(2, queries.Count);
        var mentioned = queries.SelectMany(query => query.Text.Split(" OR ", StringSplitOptions.TrimEntries)).ToList();
        Assert.Equal(30, mentioned.Count);
        Assert.Equal(30, mentioned.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void YouTubeUsesEighteenSearchesAndSixteenNames()
    {
        var queries = FeedDiscoveryPlanner.SelectForPlatform("youtube", MondayKinshasa, 18, Names(50));
        var names = queries.Where(query => query.Lane == FeedDiscoveryPlanner.Lane.Catalogue).Select(query => query.Text).ToList();

        Assert.Equal(18, queries.Count);
        Assert.Equal(16, names.Count);
        Assert.Equal(16, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void XQueryKeepsItsSafetyFilterAndAddsCatalogueNames()
    {
        const string existing =
            "(congolais OR kinshasa) lang:fr min_faves:5 -politique -élection";

        var query = FeedDiscoveryPlanner.BuildXQuery(existing, MondayKinshasa, Names(40));

        Assert.EndsWith(" lang:fr min_faves:5 -politique -élection", query);
        var people = Enumerable.Range(1, 40).Count(index => query.Contains($"\"Person {index}\"", StringComparison.Ordinal));
        Assert.Equal(10, people);
        Assert.Contains("insolite", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("weekend kinshasa", query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Count(query, " lang:"));
    }

    [Fact]
    public void CatalogueIsIncludedOnEachPlatform()
    {
        var candidates = FeedCandidateCatalogue.Load();

        Assert.Equal(250, candidates.Count);
        Assert.Equal(100, candidates.Count(candidate => candidate.Platform == "tiktok"));
        Assert.Equal(50, candidates.Count(candidate => candidate.Platform == "youtube"));
        Assert.Equal(60, candidates.Count(candidate => candidate.Platform == "facebook"));
        Assert.Equal(40, candidates.Count(candidate => candidate.Platform == "x"));
        Assert.All(candidates, candidate => Assert.Equal("included", candidate.Status));
        Assert.Contains(candidates, candidate => candidate.Name == "Boris Becker TV" && candidate.Platform == "tiktok");
    }

    [Fact]
    public void KnownSearchQueryIsKeptForAMatchingName()
    {
        var registry = new[]
        {
            new SourceRegistryEntry
            {
                Name = "Boris Becker TV",
                Import = true,
                CommunitySlug = "influence",
                SearchQuery = "\"Boris Becker TV\" OR borisbeckertv",
                Tiktok = "borisbeckertv",
            },
        };

        var subject = FeedCandidateCatalogue.Subjects("tiktok", registry)
            .Single(item => item.Name == "Boris Becker TV");

        Assert.Equal("\"Boris Becker TV\" OR borisbeckertv", subject.Query);
        Assert.Equal("influence", subject.CommunitySlug);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
