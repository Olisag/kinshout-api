using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services.WhatsApp;

public sealed class WhatsAppPublishingEngine(
    KinshoutDbContext db,
    IOptions<WhatsAppPublishingSettings> settings,
    IOpenMeteoForecastClient weather,
    IApifyTrafficClient traffic,
    IWhatsAppChannelPublisher publisher,
    EditorialCandidateSource editorial,
    IKinshasaEventSource events,
    TimeProvider clock,
    ILogger<WhatsAppPublishingEngine> logger)
{
    public async Task<int> TickAsync(CancellationToken ct = default)
    {
        var config = settings.Value;
        if (!config.Enabled)
            return 0;
        await EnsureControlAsync(ct);
        if (await db.WhatsAppChannelControls.AnyAsync(x => x.Id == 1 && x.Paused, ct))
            return 0;

        var utc = clock.GetUtcNow().UtcDateTime;
        var handled = 0;
        foreach (var slot in WhatsAppSchedule.Open(utc, config))
        {
            if (await AdvanceAsync(slot, utc, manual: false, ct))
                handled++;
        }
        await ReconcileAsync(ct);
        return handled;
    }

    public async Task<WhatsAppPublication?> PublishSlotAsync(string slotId, CancellationToken ct = default)
    {
        var slot = WhatsAppSchedule.All.FirstOrDefault(s => s.Id == slotId);
        if (slot is null)
            return null;
        var utc = clock.GetUtcNow().UtcDateTime;
        await EnsureControlAsync(ct);
        await AdvanceAsync(slot, utc, manual: true, ct);
        var key = Key(slot, utc);
        return await db.WhatsAppPublications.FirstOrDefaultAsync(x => x.IdempotencyKey == key, ct);
    }

    public async Task<string?> PreviewWeatherAsync(CancellationToken ct = default)
    {
        var local = Local(clock.GetUtcNow().UtcDateTime);
        var day = await weather.GetKinshasaAsync(local, ct);
        return day is null ? null : WeatherBulletinComposer.Compose(day, settings.Value.WeatherAttributionRequired).Body;
    }

    public static string? Destination(WhatsAppPublishingSettings config)
    {
        var id = (config.UseTestDestination ? config.TestChannelId : config.ChannelId).Trim();
        if (id.Length == 0 || !id.EndsWith("@newsletter", StringComparison.Ordinal))
            return null;
        return id;
    }

    private async Task<bool> AdvanceAsync(WhatsAppSlot slot, DateTime utc, bool manual, CancellationToken ct)
    {
        var key = Key(slot, utc);
        var row = await db.WhatsAppPublications.FirstOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (row is null)
        {
            row = new WhatsAppPublication
            {
                IdempotencyKey = key,
                ChannelId = Destination(settings.Value) ?? settings.Value.ChannelId,
                Category = slot.Category,
                Purpose = slot.Purpose,
                Slot = slot.Id,
                LocalDate = Local(utc).ToString("yyyy-MM-dd"),
                ScheduledAtUtc = WhatsAppSchedule.ScheduledUtc(utc, slot),
                CreatedAtUtc = utc,
                Status = WhatsAppPublicationStatus.Scheduled,
                Manual = manual,
            };
            db.WhatsAppPublications.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                return false;
            }
        }

        if (row.Status is WhatsAppPublicationStatus.Published or WhatsAppPublicationStatus.Submitted
            or WhatsAppPublicationStatus.Skipped or WhatsAppPublicationStatus.Cancelled
            or WhatsAppPublicationStatus.ConfirmationUnknown or WhatsAppPublicationStatus.Ready
            or WhatsAppPublicationStatus.Expired)
            return false;

        var graceEnds = row.ScheduledAtUtc.AddMinutes(slot.GraceMinutes);
        if (!manual && utc > graceEnds && row.Status is WhatsAppPublicationStatus.Scheduled or WhatsAppPublicationStatus.Failed)
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Expired, "grace-elapsed", false, ct);
            return true;
        }

        var claimed = await db.WhatsAppPublications
            .Where(x => x.Id == row.Id && (x.Status == WhatsAppPublicationStatus.Scheduled || x.Status == WhatsAppPublicationStatus.Failed))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, WhatsAppPublicationStatus.Preparing), ct);
        if (claimed == 0)
            return false;
        db.ChangeTracker.Clear();
        row = await db.WhatsAppPublications.FirstAsync(x => x.Id == row.Id, ct);

        try
        {
            if (slot.Purpose == WhatsAppPublicationPurpose.Retrieve)
                await RetrieveAsync(row, slot, utc, ct);
            else
                await PublishAsync(row, slot, utc, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning(ex, "WhatsApp slot {Slot} failed.", slot.Id);
            await FinishAsync(row, WhatsAppPublicationStatus.Failed, ex.GetType().Name, retryable: true, ct);
        }
        return true;
    }

    private async Task RetrieveAsync(WhatsAppPublication row, WhatsAppSlot slot, DateTime utc, CancellationToken ct)
    {
        if (slot.Category == "meteo")
        {
            var day = await weather.GetKinshasaAsync(Local(utc), ct);
            if (day is null)
            {
                await FinishAsync(row, WhatsAppPublicationStatus.Failed, "weather-unavailable", true, ct);
                return;
            }
            row.ProviderName = "Open-Meteo";
            row.ObservedAtUtc = utc;
            row.TraceJson = WeatherBulletinComposer.Store(day);
            await FinishAsync(row, WhatsAppPublicationStatus.Ready, null, false, ct);
            return;
        }

        var config = settings.Value;
        if (config.DryRun || !config.TrafficRedistributionPermitted)
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Skipped, "traffic-collection-not-authorized", false, ct);
            return;
        }
        if (!await ReserveAsync("apify", row.IdempotencyKey, config.CostPerTrafficRouteUsd * TrafficRouteCatalogue.Routes.Count(r => r.Enabled), utc, ct))
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Skipped, "apify-budget", false, ct);
            return;
        }
        var measured = await traffic.MeasureAsync(utc, ct);
        row.ProviderName = "Apify";
        row.ProviderReference = measured.RunId;
        row.ObservedAtUtc = utc;
        row.TraceJson = TrafficBulletinComposer.Store(measured.Observations);
        row.EstimatedCostUsd = config.CostPerTrafficRouteUsd * TrafficRouteCatalogue.Routes.Count(r => r.Enabled);
        await FinishAsync(row, WhatsAppPublicationStatus.Ready, null, false, ct);
    }

    private async Task PublishAsync(WhatsAppPublication row, WhatsAppSlot slot, DateTime utc, CancellationToken ct)
    {
        var config = settings.Value;
        if (slot.Purpose == WhatsAppPublicationPurpose.Publish && !await SpacingAllowsAsync(utc, ct))
        {
            row.Status = WhatsAppPublicationStatus.Scheduled;
            await db.SaveChangesAsync(ct);
            return;
        }

        var prepared = slot.Category switch
        {
            "meteo" or "meteo-refresh" => await PrepareWeatherAsync(row, slot, utc, ct),
            "trafic" => await PrepareTrafficAsync(row, slot, utc, ct),
            "interaction" => await PrepareInteractionAsync(row, utc, ct),
            "evenements" => await PrepareEventsAsync(row, utc, ct),
            "highlights" => await PrepareHighlightsAsync(row, utc, ct),
            _ => await PrepareEditorialAsync(row, slot.Category, ct),
        };
        if (prepared is null)
            return;

        if (await PublishedTodayAsync(utc, ct) >= config.MaxDailyPublications)
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Skipped, "daily-limit", false, ct);
            return;
        }

        row.Body = prepared.Body;
        row.DiscussionId = prepared.DiscussionId;
        row.CanonicalUrl = prepared.Url;
        if (config.DryRun || prepared.DryRunOnly)
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Ready, config.DryRun ? "dry-run" : null, false, ct);
            return;
        }

        var destination = Destination(config);
        if (destination is null)
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Failed, "destination-rejected", false, ct);
            return;
        }
        if (!await ReserveAsync("wapito", row.IdempotencyKey + ":send", 0, utc, ct))
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Skipped, "wapito-budget", false, ct);
            return;
        }

        row.Status = WhatsAppPublicationStatus.Submitting;
        await db.SaveChangesAsync(ct);
        var result = prepared.Poll is { } poll
            ? await publisher.SendPollAsync(destination, poll.Question, poll.Options, ct)
            : await publisher.SendTextAsync(destination, prepared.Body, ct);
        if (result.Unsupported && prepared.Poll is not null)
        {
            result = await publisher.SendTextAsync(destination, AudienceInteractionCatalog.TextFallback(prepared.PollPrompt!), ct);
            row.Body = AudienceInteractionCatalog.TextFallback(prepared.PollPrompt!);
        }
        await ApplyResultAsync(row, result, utc, ct);
    }

    private async Task<Prepared?> PrepareWeatherAsync(WhatsAppPublication row, WhatsAppSlot slot, DateTime utc, CancellationToken ct)
    {
        var local = Local(utc).Date;
        var snapshot = await LatestForecastAsync(local, ct);
        if (slot.Id == "weather-refresh")
        {
            var fresh = await weather.GetKinshasaAsync(local, ct);
            if (fresh is null || snapshot is null || fresh.LocalDate.Date != local || !WeatherBulletinComposer.IsSignificant(fresh, snapshot))
            {
                await FinishAsync(row, WhatsAppPublicationStatus.Skipped, "weather-unchanged", false, ct);
                return null;
            }
            snapshot = fresh;
        }
        else if (snapshot is null)
        {
            snapshot = await weather.GetKinshasaAsync(local, ct);
        }
        if (snapshot is null || snapshot.LocalDate.Date != local)
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Skipped, snapshot is null ? "weather-unavailable" : "forecast-not-for-today", false, ct);
            return null;
        }
        var bulletin = WeatherBulletinComposer.Compose(snapshot, settings.Value.WeatherAttributionRequired);
        row.ProviderName = "Open-Meteo";
        row.ObservedAtUtc = utc;
        row.TraceJson = bulletin.TraceJson;
        return new Prepared(bulletin.Body, null, "https://kinoiserie.com", null, null, null, false);
    }

    private async Task<Prepared?> PrepareTrafficAsync(WhatsAppPublication row, WhatsAppSlot slot, DateTime utc, CancellationToken ct)
    {
        var config = settings.Value;
        var retrieveSlot = slot.Id.Replace("-publish", "-retrieve", StringComparison.Ordinal);
        var retrieval = await db.WhatsAppPublications
            .Where(p => p.LocalDate == row.LocalDate && p.Purpose == WhatsAppPublicationPurpose.Retrieve && p.Slot == retrieveSlot && p.Status == WhatsAppPublicationStatus.Ready)
            .OrderByDescending(p => p.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        var observations = TrafficBulletinComposer.Restore(retrieval?.TraceJson);
        if (observations.Count == 0)
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Skipped, "traffic-not-ready", false, ct);
            return null;
        }
        if (slot.Id.Contains("midday", StringComparison.Ordinal))
        {
            var earlier = await db.WhatsAppPublications
                .Where(p => p.LocalDate == row.LocalDate && p.Category == "trafic" && p.Purpose == WhatsAppPublicationPurpose.Retrieve && p.Id != retrieval!.Id)
                .OrderByDescending(p => p.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);
            if (!TrafficBulletinComposer.QualifiesForMidday(observations, TrafficBulletinComposer.Restore(earlier?.TraceJson), config.MinHeavySegmentsForMidday))
            {
                await FinishAsync(row, WhatsAppPublicationStatus.Skipped, "midday-not-justified", false, ct);
                return null;
            }
        }
        var body = TrafficBulletinComposer.Format(observations, DateTime.SpecifyKind(Local(utc), DateTimeKind.Unspecified), config.TrafficMaxObservationAgeMinutes, config.MinTrafficSegments, config.MaxTrafficSegments, out var reason);
        if (body is null)
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Skipped, reason, false, ct);
            return null;
        }
        if (!config.TrafficRedistributionPermitted)
        {
            row.Body = body;
            row.ProviderName = "Apify";
            row.ProviderReference = retrieval?.ProviderReference;
            row.TraceJson = retrieval?.TraceJson;
            await FinishAsync(row, WhatsAppPublicationStatus.Skipped, "traffic-redistribution-not-permitted", false, ct);
            return null;
        }
        row.ProviderName = "Apify";
        row.ProviderReference = retrieval?.ProviderReference;
        row.ObservedAtUtc = retrieval?.ObservedAtUtc;
        row.TraceJson = retrieval?.TraceJson;
        return new Prepared(body, null, "https://kinoiserie.com", null, null, null, false);
    }

    private async Task<Prepared?> PrepareEditorialAsync(WhatsAppPublication row, string category, CancellationToken ct)
    {
        var story = await editorial.NextAsync(category, clock.GetUtcNow().UtcDateTime, ct);
        if (story is null)
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Skipped, "no-eligible-story", false, ct);
            return null;
        }
        var url = EditorialComposer.PublicationUrl(settings.Value.WebBaseUrl, story.Id, category);
        var body = category switch
        {
            "essentiel" => EditorialComposer.Essentiel(story, url),
            "buzz" => EditorialComposer.Buzz(story, url),
            "insolite" => EditorialComposer.Insolite(story, url),
            "musique" => EditorialComposer.Musique(story, url),
            "fierte" => EditorialComposer.Fierte(story, url),
            _ => null,
        };
        if (body is null)
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Skipped, "empty-story", false, ct);
            return null;
        }
        return new Prepared(body, story.Id, url, null, null, null, false);
    }

    private async Task<Prepared?> PrepareHighlightsAsync(WhatsAppPublication row, DateTime utc, CancellationToken ct)
    {
        var stories = await editorial.HighlightsAsync(utc, ct);
        var items = stories.Select(s => (s.Title, EditorialComposer.PublicationUrl(settings.Value.WebBaseUrl, s.Id, "weekly_highlights"))).ToList();
        var body = EditorialComposer.Highlights(items);
        if (body is null)
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Skipped, "not-enough-highlights", false, ct);
            return null;
        }
        return new Prepared(body, stories.First().Id, items[0].Item2, null, null, null, false);
    }

    private async Task<Prepared?> PrepareEventsAsync(WhatsAppPublication row, DateTime utc, CancellationToken ct)
    {
        var headline = Local(utc).DayOfWeek switch
        {
            DayOfWeek.Friday => "LES BONS PLANS DU WEEK-END",
            DayOfWeek.Saturday => "ON FAIT QUOI AUJOURD'HUI ?",
            _ => "ON FAIT QUOI CE WEEK-END ?",
        };
        var body = EditorialComposer.Events(headline, await events.UpcomingAsync(ct), Local(utc));
        if (body is null)
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Skipped, "no-verified-event", false, ct);
            return null;
        }
        return new Prepared(body, null, "https://kinoiserie.com", null, null, null, false);
    }

    private async Task<Prepared?> PrepareInteractionAsync(WhatsAppPublication row, DateTime utc, CancellationToken ct)
    {
        var used = await editorial.UsedPromptIdsAsync(utc, ct);
        var prompt = AudienceInteractionCatalog.Next(Local(utc).DayOfWeek, used);
        if (prompt is null)
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Skipped, "no-unused-prompt", false, ct);
            return null;
        }
        row.ProviderReference = prompt.Id;
        if (prompt.Options.Length >= 2)
            return new Prepared(AudienceInteractionCatalog.TextFallback(prompt), null, null, prompt.Text, prompt.Options, prompt, false);
        return new Prepared(prompt.Text, null, null, null, null, prompt, false);
    }

    private async Task ApplyResultAsync(WhatsAppPublication row, ChannelPublishResult result, DateTime utc, CancellationToken ct)
    {
        if (result.Ambiguous)
        {
            row.ProviderMessageId = result.MessageId;
            await FinishAsync(row, WhatsAppPublicationStatus.ConfirmationUnknown, result.Error, false, ct);
            return;
        }
        if (!result.Accepted)
        {
            await FinishAsync(row, WhatsAppPublicationStatus.Failed, result.Error, result.Retryable && row.RetryCount < settings.Value.MaximumRetryAttempts, ct);
            return;
        }
        row.ProviderMessageId = result.MessageId;
        row.SubmittedAtUtc = utc;
        await FinishAsync(row, WhatsAppPublicationStatus.Submitted, null, false, ct);
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var pending = await db.WhatsAppPublications
            .Where(p => p.Status == WhatsAppPublicationStatus.Submitted && p.ProviderMessageId != null)
            .OrderBy(p => p.SubmittedAtUtc)
            .Take(3)
            .ToListAsync(ct);
        var destination = Destination(settings.Value);
        if (destination is null)
            return;
        foreach (var row in pending)
        {
            if (!await ReserveAsync("wapito", row.IdempotencyKey + ":status", 0, clock.GetUtcNow().UtcDateTime, ct))
                return;
            var exists = await publisher.MessageExistsAsync(destination, row.ProviderMessageId!, ct);
            if (!exists)
                continue;
            row.Status = WhatsAppPublicationStatus.Published;
            row.ConfirmedAtUtc = clock.GetUtcNow().UtcDateTime;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<WeatherDay?> LatestForecastAsync(DateTime localDate, CancellationToken ct)
    {
        var date = localDate.ToString("yyyy-MM-dd");
        var row = await db.WhatsAppPublications
            .Where(p => p.LocalDate == date && p.Category == "meteo" && p.Purpose == WhatsAppPublicationPurpose.Retrieve && p.Status == WhatsAppPublicationStatus.Ready)
            .OrderByDescending(p => p.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        var restored = WeatherBulletinComposer.Restore(row?.TraceJson);
        return restored is null || restored.LocalDate.Date != localDate.Date ? null : restored;
    }

    private async Task<int> PublishedTodayAsync(DateTime utc, CancellationToken ct)
    {
        var date = Local(utc).ToString("yyyy-MM-dd");
        return await db.WhatsAppPublications.CountAsync(p =>
            p.LocalDate == date
            && p.Purpose == WhatsAppPublicationPurpose.Publish
            && (p.Status == WhatsAppPublicationStatus.Ready
                || p.Status == WhatsAppPublicationStatus.Submitting
                || p.Status == WhatsAppPublicationStatus.Submitted
                || p.Status == WhatsAppPublicationStatus.Published
                || p.Status == WhatsAppPublicationStatus.ConfirmationUnknown), ct);
    }

    private async Task<bool> SpacingAllowsAsync(DateTime utc, CancellationToken ct)
    {
        var spacing = TimeSpan.FromMinutes(Math.Max(0, settings.Value.MinimumPublicationSpacingMinutes));
        var latest = await db.WhatsAppPublications
            .Where(p => p.Purpose == WhatsAppPublicationPurpose.Publish && p.SubmittedAtUtc != null)
            .OrderByDescending(p => p.SubmittedAtUtc)
            .Select(p => p.SubmittedAtUtc)
            .FirstOrDefaultAsync(ct);
        return latest is null || utc - latest.Value >= spacing;
    }

    private async Task<bool> ReserveAsync(string provider, string slotKey, decimal cost, DateTime utc, CancellationToken ct)
    {
        var config = settings.Value;
        if (await db.WhatsAppSpends.AnyAsync(x => x.SlotKey == slotKey, ct))
            return true;
        var monthStart = new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (provider == "apify")
        {
            var month = await db.WhatsAppSpends.Where(x => x.Provider == "apify" && x.OccurredAtUtc >= monthStart).SumAsync(x => x.EstimatedCostUsd, ct);
            var day = Local(utc).ToString("yyyy-MM-dd");
            var runs = await db.WhatsAppSpends.CountAsync(x => x.Provider == "apify" && x.Kind == day, ct);
            if (month + cost > config.MaxMonthlyApifySpend || runs >= config.MaxApifyRunsPerDay)
                return false;
            var daySpend = await db.WhatsAppSpends.Where(x => x.Provider == "apify" && x.Kind == day).SumAsync(x => x.EstimatedCostUsd, ct);
            if (daySpend + cost > config.MaxDailyApifySpend)
                return false;
        }
        if (provider == "wapito")
        {
            var requests = await db.WhatsAppSpends.CountAsync(x => x.Provider == "wapito" && x.OccurredAtUtc >= monthStart, ct);
            if (requests >= config.MaxMonthlyWapitoRequests)
                return false;
        }
        db.WhatsAppSpends.Add(new WhatsAppSpend
        {
            Provider = provider,
            SlotKey = slotKey,
            OccurredAtUtc = utc,
            EstimatedCostUsd = cost,
            Kind = Local(utc).ToString("yyyy-MM-dd"),
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return await db.WhatsAppSpends.AnyAsync(x => x.SlotKey == slotKey, ct);
        }
    }

    private async Task FinishAsync(WhatsAppPublication row, string status, string? error, bool retryable, CancellationToken ct)
    {
        if (status == WhatsAppPublicationStatus.Failed)
            row.RetryCount += 1;
        row.Status = status;
        row.LastError = error;
        row.Retryable = retryable && row.RetryCount <= settings.Value.MaximumRetryAttempts;
        await db.SaveChangesAsync(ct);
        if (status == WhatsAppPublicationStatus.Failed && !row.Retryable)
            logger.LogError("WhatsApp slot {Slot} stopped after {Error}.", row.Slot, error);
    }

    private async Task EnsureControlAsync(CancellationToken ct)
    {
        if (await db.WhatsAppChannelControls.AnyAsync(x => x.Id == 1, ct))
            return;
        db.WhatsAppChannelControls.Add(new WhatsAppChannelControl { Id = 1, Paused = false });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }

    private string Key(WhatsAppSlot slot, DateTime utc) =>
        WhatsAppSchedule.Key(Destination(settings.Value) ?? settings.Value.ChannelId, slot, Local(utc));

    private static DateTime Local(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), FeedImportClock.Kinshasa());

    private sealed record Prepared(
        string Body,
        Guid? DiscussionId,
        string? Url,
        string? PollQuestion,
        string[]? PollOptions,
        AudiencePrompt? PollPrompt,
        bool DryRunOnly)
    {
        public (string Question, string[] Options)? Poll =>
            PollQuestion is not null && PollOptions is { Length: >= 2 } ? (PollQuestion, PollOptions) : null;
    }
}
