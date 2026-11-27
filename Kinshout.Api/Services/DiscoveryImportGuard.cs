using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

/// <summary>
/// The v1.5 allowances that are already approved. A run shares the period with the other clocks.
/// </summary>
public static class DiscoveryImportGuard
{
    public const int FacebookWeeklyImports = 16;
    public const int FacebookWeeklyResults = 60;
    public const int XDailyImports = 30;
    public const int XDailyResults = 30;
    public const int TikTokDailyImports = 25;
    public const int TikTokDailyResults = 192;
    public const int PersonalityDailyImports = 2;

    public static async Task<bool> CanImportAsync(
        KinshoutDbContext db,
        string provider,
        string? author,
        DateTime utcNow,
        CancellationToken ct)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
            FeedImportClock.Kinshasa());
        var dayStart = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified),
            FeedImportClock.Kinshasa());

        if (!string.IsNullOrWhiteSpace(author))
        {
            var name = author.Trim().ToLowerInvariant();
            var todaysAuthors = await db.Discussions.AsNoTracking()
                .Where(d => d.SourceImportedAt >= dayStart)
                .Select(d => new { d.SourceProvider, d.SourceOriginalAuthor })
                .ToListAsync(ct);
            var byAuthor = todaysAuthors.Count(row =>
                row.SourceProvider is not null
                && !row.SourceProvider.Equals(DiscussionSourceProvider.Kinshout, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(row.SourceOriginalAuthor)
                && row.SourceOriginalAuthor.Trim().ToLowerInvariant() == name);
            if (byAuthor >= PersonalityDailyImports)
                return false;
        }

        var (normalized, periodStart, cap) = Period(provider, local, dayStart);
        if (cap is null)
            return true;

        var imported = await db.Discussions.CountAsync(
            d => d.SourceProvider == normalized && d.SourceImportedAt >= periodStart,
            ct);
        return imported < cap;
    }

    public static async Task<DiscoveryBudget> BudgetAsync(
        KinshoutDbContext db,
        string provider,
        DateTime utcNow,
        CancellationToken ct)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
            FeedImportClock.Kinshasa());
        var dayStart = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified),
            FeedImportClock.Kinshasa());
        var (normalized, periodStart, importCap) = Period(provider, local, dayStart);
        var resultCap = ResultCap(normalized);
        if (importCap is null || resultCap is null)
            return new DiscoveryBudget(int.MaxValue, int.MaxValue, [], []);

        var imported = await db.Discussions.CountAsync(
            d => d.SourceProvider == normalized && d.SourceImportedAt >= periodStart,
            ct);
        var key = PeriodKey(normalized, local);
        var spent = await db.DiscoverySpends
            .Where(row => row.PeriodKey == key)
            .Select(row => row.ResultsRequested)
            .FirstOrDefaultAsync(ct);
        var rows = await db.DiscoverySourceStats
            .Where(row => row.Platform == normalized)
            .ToListAsync(ct);
        var sources = rows
            .OrderByDescending(row => row.Kept)
            .ThenBy(row => row.Handle)
            .Select(row => new DiscoverySourceScore(row.Handle, row.Checks, row.Kept, row.Imports))
            .ToList();
        var next = normalized == DiscussionSourceProvider.TikTok
            ? SelectProfiles(
                rows.Select(row => new ProfileCandidate(row.Handle, row.Kept, row.Checks, row.LastCheckedAt, row.Verified)).ToList(),
                utcNow,
                4)
            : [];
        return new DiscoveryBudget(
            Math.Max(0, importCap.Value - imported),
            Math.Max(0, resultCap.Value - spent),
            sources,
            next);
    }

    /// <summary>
    /// The next profile reads. A never-checked account goes first. A productive
    /// account is seen sooner than a quiet one, and a quiet one that has waited
    /// long enough still comes ahead of an account checked today.
    /// </summary>
    public static IReadOnlyList<string> SelectProfiles(
        IReadOnlyList<ProfileCandidate> sources,
        DateTime utcNow,
        int count)
    {
        if (count < 1)
            return [];

        return sources
            .Where(source => source.Verified || source.Checks > 0)
            .Select(source => (source, Handle: source.Handle.Trim().TrimStart('@')))
            .Where(source => source.Handle.Length > 0)
            .GroupBy(source => source.Handle, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(source => ProfilePriority(source.source, utcNow))
            .ThenBy(source => source.Handle, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .Select(source => source.Handle)
            .ToList();
    }

    private static double ProfilePriority(ProfileCandidate source, DateTime utcNow)
    {
        if (source.Checks <= 0 || source.LastCheckedAt == default)
            return double.MaxValue;

        var hours = (utcNow - source.LastCheckedAt).TotalHours;
        if (hours < 0)
            hours = 0;
        return hours + (source.Kept > 0 ? 18 : 0);
    }

    public static async Task RecordSpendAsync(
        KinshoutDbContext db,
        string provider,
        int resultsRequested,
        IReadOnlyList<DiscoveryCheckNote> checks,
        IReadOnlyList<string> discovered,
        DateTime utcNow,
        CancellationToken ct)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
            FeedImportClock.Kinshasa());
        var normalized = DiscussionSourceProvider.Normalize(provider);
        if (resultsRequested > 0)
        {
            var key = PeriodKey(normalized, local);
            var row = await db.DiscoverySpends.FirstOrDefaultAsync(item => item.PeriodKey == key, ct);
            if (row is null)
            {
                row = new DiscoverySpend { PeriodKey = key, ResultsRequested = resultsRequested };
                db.DiscoverySpends.Add(row);
            }
            else
            {
                row.ResultsRequested += resultsRequested;
            }
        }

        foreach (var check in checks)
        {
            if (string.IsNullOrWhiteSpace(check.Handle))
                continue;
            var handle = check.Handle.Trim().TrimStart('@');
            var statKey = normalized + ":" + handle.ToLowerInvariant();
            var stat = await db.DiscoverySourceStats.FirstOrDefaultAsync(item => item.Key == statKey, ct);
            if (stat is null)
            {
                stat = new DiscoverySourceStat
                {
                    Key = statKey,
                    Platform = normalized,
                    Handle = handle,
                };
                db.DiscoverySourceStats.Add(stat);
            }

            stat.Checks++;
            stat.Kept += Math.Max(0, check.Kept);
            stat.Verified = true;
            stat.LastCheckedAt = utcNow;
        }

        foreach (var found in discovered)
        {
            if (string.IsNullOrWhiteSpace(found))
                continue;
            var handle = found.Trim().TrimStart('@');
            if (handle.Length == 0)
                continue;
            var statKey = normalized + ":" + handle.ToLowerInvariant();
            var stat = await db.DiscoverySourceStats.FirstOrDefaultAsync(item => item.Key == statKey, ct);
            if (stat is null)
            {
                stat = new DiscoverySourceStat
                {
                    Key = statKey,
                    Platform = normalized,
                    Handle = handle,
                    Verified = true,
                    LastCheckedAt = default,
                };
                db.DiscoverySourceStats.Add(stat);
            }
            else if (!stat.Verified)
            {
                stat.Verified = true;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public static async Task NoteImportAsync(
        KinshoutDbContext db,
        string provider,
        string? author,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(author))
            return;

        var normalized = DiscussionSourceProvider.Normalize(provider);
        var handle = author.Trim().TrimStart('@');
        var statKey = normalized + ":" + handle.ToLowerInvariant();
        var stat = await db.DiscoverySourceStats.FirstOrDefaultAsync(item => item.Key == statKey, ct);
        if (stat is null)
        {
            stat = new DiscoverySourceStat
            {
                Key = statKey,
                Platform = normalized,
                Handle = handle,
            };
            db.DiscoverySourceStats.Add(stat);
        }

        stat.Imports++;
        await db.SaveChangesAsync(ct);
    }

    private static (string Provider, DateTime Start, int? ImportCap) Period(
        string provider,
        DateTime local,
        DateTime dayStartUtc)
    {
        var normalized = DiscussionSourceProvider.Normalize(provider);
        if (normalized == DiscussionSourceProvider.Facebook)
        {
            var monday = local.Date.AddDays(-((int)local.DayOfWeek + 6) % 7);
            var start = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(monday, DateTimeKind.Unspecified),
                FeedImportClock.Kinshasa());
            return (normalized, start, FacebookWeeklyImports);
        }

        if (normalized == DiscussionSourceProvider.Twitter)
            return (normalized, dayStartUtc, XDailyImports);
        if (normalized == DiscussionSourceProvider.TikTok)
            return (normalized, dayStartUtc, TikTokDailyImports);
        return (normalized, dayStartUtc, null);
    }

    private static int? ResultCap(string provider) => provider switch
    {
        DiscussionSourceProvider.Facebook => FacebookWeeklyResults,
        DiscussionSourceProvider.Twitter => XDailyResults,
        DiscussionSourceProvider.TikTok => TikTokDailyResults,
        _ => null,
    };

    public static string PeriodKey(string provider, DateTime local)
    {
        var normalized = DiscussionSourceProvider.Normalize(provider);
        if (normalized == DiscussionSourceProvider.Facebook)
        {
            var monday = local.Date.AddDays(-((int)local.DayOfWeek + 6) % 7);
            return "facebook:" + monday.ToString("yyyy-MM-dd");
        }

        return normalized + ":" + local.ToString("yyyy-MM-dd");
    }
}

public sealed record DiscoveryBudget(
    int RemainingImports,
    int RemainingResults,
    IReadOnlyList<DiscoverySourceScore> Sources,
    IReadOnlyList<string> NextProfiles);

public sealed record DiscoverySourceScore(string Handle, int Checks, int Kept, int Imports);

public sealed record DiscoveryCheckNote(string Handle, int Kept);

public readonly record struct ProfileCandidate(
    string Handle,
    int Kept,
    int Checks,
    DateTime LastCheckedAt,
    bool Verified);

public sealed record DiscoverySpendRequest(
    int ResultsRequested,
    IReadOnlyList<DiscoveryCheckNote>? Checks,
    IReadOnlyList<string>? Discovered);
