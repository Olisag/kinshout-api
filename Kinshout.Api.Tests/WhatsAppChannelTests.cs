using Kinshout.Api.Configuration;
using Kinshout.Api.Models;
using Kinshout.Api.Services.WhatsApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Tests;

public class WhatsAppChannelTests
{
    [Fact]
    public void WeatherPeriodsDoNotShareHoursAndOnlyThePeakShowsRainRisk()
    {
        var day = new WeatherDay(new DateTime(2026, 11, 28), 24, 30, 29, 34, Hours());
        var body = WeatherBulletinComposer.Compose(day, attribute: false).Body;

        Assert.Contains("24°C – 30°C", body, StringComparison.Ordinal);
        Assert.Contains("☁️ *Matin | 06h – 12h*", body, StringComparison.Ordinal);
        Assert.Contains("24°C – 27°C · Nuageux", body, StringComparison.Ordinal);
        Assert.Contains("Risque de pluie jusqu'à 25 %", body, StringComparison.Ordinal);
        Assert.Contains("Une petite bruine est prévue vers 10h.", body, StringComparison.Ordinal);
        Assert.Contains("🌤️ *Après-midi | 12h – 18h*", body, StringComparison.Ordinal);
        Assert.Contains("28°C – 30°C · Partiellement nuageux", body, StringComparison.Ordinal);
        Assert.DoesNotContain("80 %", body, StringComparison.Ordinal);
        Assert.DoesNotContain("orage", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("On ressent jusqu'à 34°C.", body, StringComparison.Ordinal);
        Assert.Contains("https://kinoiserie.com", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Open-Meteo", body, StringComparison.Ordinal);
        Assert.Equal(1, Count(body, "Risque de pluie"));
    }

    [Fact]
    public void WeatherAttributionIsAddedOnlyWhenRequired()
    {
        var day = new WeatherDay(new DateTime(2026, 11, 28), 24, 30, 29, 34, Hours().Take(18).ToList());
        var body = WeatherBulletinComposer.Compose(day, attribute: true).Body;
        Assert.Contains("Météo : Open-Meteo", body, StringComparison.Ordinal);
    }

    [Fact]
    public void StormIsDescribedAsARisk()
    {
        var hours = new List<WeatherHour>
        {
            new(new DateTime(2026, 11, 28, 19, 0, 0), 25, 26, 80, 4, 95, 10),
        };
        var day = new WeatherDay(hours[0].Local.Date, 24, 26, 25, 27, hours);
        var body = WeatherBulletinComposer.Compose(day, false).Body;
        Assert.Contains("Risque d'orages", body, StringComparison.Ordinal);
        Assert.Contains("Des orages sont annoncés", body, StringComparison.Ordinal);
        Assert.DoesNotContain("orage confirmé", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrafficMinutesAddUpAndAMissingReferenceDoesNotInventABaseline()
    {
        Assert.Equal(42, TrafficBulletinComposer.WholeMinutes(2541));
        Assert.Equal(21, TrafficBulletinComposer.WholeMinutes(1230));
        Assert.Equal(5, TrafficBulletinComposer.WholeMinutes(305));
        Assert.Equal(5, TrafficBulletinComposer.WholeMinutes(277));

        var heavy = new TrafficObservation("a", "Boulevard Lumumba", "Limete → Ndjili", 2541, 1230, "heavy", false, DateTime.UtcNow, "lumumba");
        Assert.Equal(TrafficClass.Heavy, TrafficBulletinComposer.Classify(heavy));
        var ratio = new TrafficObservation("b", "By-Pass", "Ngaba → Lemba", 1800, 1000, null, false, DateTime.UtcNow, "bypass");
        Assert.Equal(TrafficClass.Busy, TrafficBulletinComposer.Classify(ratio));
        var noReference = new TrafficObservation("c", "By-Pass", "Ngaba → Lemba", 600, null, null, false, DateTime.UtcNow, "bypass");
        Assert.Equal(TrafficClass.Fluid, TrafficBulletinComposer.Classify(noReference));
        var missing = new TrafficObservation("d", "By-Pass", "Ngaba → Lemba", null, null, null, false, DateTime.UtcNow, null);
        Assert.Equal(TrafficClass.Unavailable, TrafficBulletinComposer.Classify(missing));
    }

    [Fact]
    public void StaleOrThinTrafficIsNotPublished()
    {
        var old = DateTime.UtcNow.AddMinutes(-45);
        var observations = Enumerable.Range(0, 4).Select(i =>
            new TrafficObservation($"r{i}", "Route", "A → B", 600, 500, "light", false, old, "route")).ToList();
        var body = TrafficBulletinComposer.Format(observations, DateTime.UtcNow, 30, 3, 10, out var reason);
        Assert.Null(body);
        Assert.Equal("not-enough-fresh-segments", reason);
    }

    [Fact]
    public void ProvenTrafficReadingsKeepTheRoadByRoadLayout()
    {
        var observed = new DateTime(2026, 11, 28, 5, 24, 0, DateTimeKind.Utc);
        var rows = new List<TrafficObservation>
        {
            new("lumumba-ndjili", "Boulevard Lumumba", "Échangeur de Limete → Aéroport de Ndjili", 2541, 1230, "heavy", false, observed, "Boulevard Lumumba"),
            new("juin", "Boulevard du 30 Juin", "Safricas → Cercle Elaïs", 432, 435, "light", false, observed, "30 Juin"),
            new("novembre", "Avenue du 24 Novembre", "Batetela → Stade des Martyrs", 915, 810, "light", false, observed, "24 Novembre"),
        };
        var body = TrafficBulletinComposer.Format(rows, new DateTime(2026, 11, 28, 6, 24, 0), 30, 3, 10, out var reason);
        Assert.Equal("", reason);
        Assert.NotNull(body);
        Assert.Contains("🔴 *Boulevard Lumumba*", body, StringComparison.Ordinal);
        Assert.Contains("42 min au lieu de 21 min (+21 min)", body, StringComparison.Ordinal);
        Assert.Contains("15 min au lieu de 14 min (+1 min)", body, StringComparison.Ordinal);
        Assert.Contains("Circulation fluide.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Google", body, StringComparison.Ordinal);
        Assert.StartsWith("🚦 KIN TRAFIC | by Kinoiserie", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MiddayNeedsTwoHeavySegmentsOrARealChange()
    {
        var now = DateTime.UtcNow;
        var one = new TrafficObservation("a", "Lumumba", "A → B", 2000, 800, "heavy", false, now, "lumumba");
        var calm = new TrafficObservation("b", "Juin", "A → B", 400, 400, "light", false, now, "juin");
        Assert.False(TrafficBulletinComposer.QualifiesForMidday([one, calm], null, 2));
        var two = new TrafficObservation("c", "By-Pass", "A → B", 2000, 700, "heavy", false, now, "bypass");
        Assert.True(TrafficBulletinComposer.QualifiesForMidday([one, two], null, 2));
    }

    [Fact]
    public void ScheduleUsesKinshasaAndSundayReplacesTheMusicSlot()
    {
        var settings = new WhatsAppPublishingSettings();
        var saturdayMorning = new DateTime(2026, 11, 28, 4, 30, 0, DateTimeKind.Utc);
        var saturday = WhatsAppSchedule.Open(saturdayMorning, settings).Select(s => s.Id).ToList();
        Assert.Contains("weather-publish", saturday);
        Assert.DoesNotContain("editorial-musique", saturday);
        Assert.DoesNotContain("traffic-midday-publish", saturday);
        Assert.DoesNotContain("weather-refresh", saturday);

        var saturdayEvening = new DateTime(2026, 11, 28, 17, 30, 0, DateTimeKind.Utc);
        var evening = WhatsAppSchedule.Open(saturdayEvening, settings).Select(s => s.Id).ToList();
        Assert.Contains("editorial-musique", evening);
        Assert.DoesNotContain("editorial-highlights", evening);

        var sundayEvening = new DateTime(2026, 11, 29, 17, 30, 0, DateTimeKind.Utc);
        var sunday = WhatsAppSchedule.Open(sundayEvening, settings).Select(s => s.Id).ToList();
        Assert.Contains("editorial-highlights", sunday);
        Assert.DoesNotContain("editorial-musique", sunday);
    }

    [Fact]
    public void InteractionDoesNotRepeatAUsedPromptAndDoesNotAskForAReply()
    {
        var first = AudienceInteractionCatalog.Next(DayOfWeek.Tuesday, []);
        var second = AudienceInteractionCatalog.Next(DayOfWeek.Tuesday, [first!.Id]);
        Assert.NotEqual(first.Id, second!.Id);
        Assert.Null(AudienceInteractionCatalog.Next(DayOfWeek.Tuesday, ["quiz-mbote", "quiz-fleuve"]));
        var text = AudienceInteractionCatalog.TextFallback(first);
        Assert.Contains("Bonjour", text, StringComparison.Ordinal);
        Assert.DoesNotContain("répondez", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DestinationRejectsGroupsAndPhoneNumbers()
    {
        Assert.Null(WhatsAppPublishingEngine.Destination(new WhatsAppPublishingSettings { ChannelId = "120363431365032494@g.us" }));
        Assert.Null(WhatsAppPublishingEngine.Destination(new WhatsAppPublishingSettings { ChannelId = "27716723618" }));
        Assert.Equal("120363412372960130@newsletter", WhatsAppPublishingEngine.Destination(new WhatsAppPublishingSettings()));
    }

    [Fact]
    public void ABadGeocodeDoesNotMatchTheRequestedRoad()
    {
        var sendwe = TrafficRouteCatalogue.Routes.Single(r => r.Id == "sendwe");
        Assert.False(sendwe.Enabled);
        var lumumba = TrafficRouteCatalogue.Routes.Single(r => r.Id == "lumumba-ndjili");
        Assert.True(TrafficRouteCatalogue.ViaMatches(lumumba, "Boulevard Lumumba / N1"));
        Assert.False(TrafficRouteCatalogue.ViaMatches(lumumba, "Avenue Nguma"));
    }

    [Fact]
    public async Task DryRunPreparesOneWeatherBulletinAndDoesNotSendItTwice()
    {
        await using var db = await TestDbFactory.CreateSqliteAsync();
        var publisher = new RecordingPublisher();
        var engine = Engine(db, publisher, new DateTime(2026, 11, 28, 4, 30, 0, DateTimeKind.Utc), dryRun: true);
        await engine.TickAsync();
        await engine.TickAsync();

        var rows = await db.WhatsAppPublications.Where(x => x.Slot == "weather-publish").ToListAsync();
        Assert.Single(rows);
        Assert.Equal(WhatsAppPublicationStatus.Ready, rows[0].Status);
        Assert.Contains("KIN MÉTÉO", rows[0].Body, StringComparison.Ordinal);
        Assert.Empty(publisher.Texts);
    }

    [Fact]
    public async Task EditorialSkipsBlockedUnreviewedAndAlreadyPublishedStories()
    {
        await using var db = await TestDbFactory.CreateSqliteAsync();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var community = await TestDbFactory.SeedCommunityAsync(db, user, "ville");
        var now = new DateTime(2026, 11, 28, 8, 30, 0, DateTimeKind.Utc);
        db.Discussions.AddRange(
            Story(user.Id, community.Id, "ville", "Eau coupée à Limete", now.AddHours(-2), blocked: true),
            Story(user.Id, community.Id, "influence", "Une vidéo circule", now.AddHours(-3), provider: "tiktok", reviewed: false),
            Story(user.Id, community.Id, "ville", "Le marché de Gambela ouvre plus tôt", now.AddHours(-1)));
        await db.SaveChangesAsync();

        var source = new EditorialCandidateSource(db, Options.Create(new WhatsAppPublishingSettings()));
        var story = await source.NextAsync("essentiel", now, CancellationToken.None);
        Assert.NotNull(story);
        Assert.Contains("Gambela", story.Title, StringComparison.Ordinal);

        db.WhatsAppPublications.Add(new WhatsAppPublication
        {
            IdempotencyKey = "used",
            ChannelId = "120363412372960130@newsletter",
            Category = "essentiel",
            Purpose = WhatsAppPublicationPurpose.Publish,
            Slot = "editorial-essentiel",
            LocalDate = "2026-11-28",
            ScheduledAtUtc = now,
            CreatedAtUtc = now,
            Status = WhatsAppPublicationStatus.Published,
            DiscussionId = story.Id,
        });
        await db.SaveChangesAsync();
        Assert.Null(await source.NextAsync("essentiel", now, CancellationToken.None));
    }

    [Fact]
    public void EditorialLinkKeepsTheExistingDiscussionRoute()
    {
        var id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var url = EditorialComposer.PublicationUrl("https://kinoiserie.com", id, "buzz");
        Assert.Equal($"https://kinoiserie.com/movie/details/{id}?utm_source=whatsapp&utm_medium=channel&utm_campaign=buzz", url);
    }

    [Fact]
    public async Task AnEarlyManualTrafficPostLeavesTheAfternoonSlotFree()
    {
        await using var db = await TestDbFactory.CreateSqliteAsync();
        var early = new DateTime(2026, 11, 28, 8, 5, 0, DateTimeKind.Utc);
        var retrieve = WhatsAppSchedule.All.Single(s => s.Id == "traffic-afternoon-retrieve");
        var publish = WhatsAppSchedule.All.Single(s => s.Id == "traffic-afternoon-publish");
        var channel = "120363412372960130@newsletter";
        db.WhatsAppPublications.AddRange(
            ManualRow(channel, retrieve, early, WhatsAppPublicationStatus.Ready),
            ManualRow(channel, publish, early, WhatsAppPublicationStatus.Submitted));
        await db.SaveChangesAsync();

        await Engine(db, new RecordingPublisher(), early, dryRun: false, traffic: true).TickAsync();
        var parked = await db.WhatsAppPublications.OrderBy(x => x.Slot).ToListAsync();
        Assert.All(parked, row => Assert.EndsWith("|early", row.IdempotencyKey, StringComparison.Ordinal));
        Assert.Contains(parked, row => row.Slot == "traffic-afternoon-publish" && row.Status == WhatsAppPublicationStatus.Submitted);

        var duringSlot = new DateTime(2026, 11, 28, 14, 30, 0, DateTimeKind.Utc);
        await Engine(db, new RecordingPublisher(), duringSlot, dryRun: false, traffic: true).TickAsync();
        var afternoon = await db.WhatsAppPublications.Where(x => x.Slot == "traffic-afternoon-publish").ToListAsync();
        Assert.Equal(2, afternoon.Count);
        Assert.Contains(afternoon, row => row.Status == WhatsAppPublicationStatus.Submitted && row.IdempotencyKey.EndsWith("|early", StringComparison.Ordinal));
        Assert.Contains(afternoon, row => row.IdempotencyKey == WhatsAppSchedule.Key(channel, publish, duringSlot));
    }

    [Fact]
    public async Task DisabledEngineDoesNothing()
    {
        await using var db = await TestDbFactory.CreateSqliteAsync();
        var engine = Engine(db, new RecordingPublisher(), DateTime.UtcNow, dryRun: true, enabled: false);
        Assert.Equal(0, await engine.TickAsync());
        Assert.Empty(await db.WhatsAppPublications.ToListAsync());
    }

    private static WhatsAppPublication ManualRow(string channel, WhatsAppSlot slot, DateTime utc, string status) =>
        new()
        {
            IdempotencyKey = WhatsAppSchedule.Key(channel, slot, utc),
            ChannelId = channel,
            Category = slot.Category,
            Purpose = slot.Purpose,
            Slot = slot.Id,
            LocalDate = "2026-11-28",
            ScheduledAtUtc = WhatsAppSchedule.ScheduledUtc(utc, slot),
            CreatedAtUtc = utc,
            Status = status,
            Manual = true,
            ProviderMessageId = status == WhatsAppPublicationStatus.Submitted ? "early-message" : null,
        };

    private static WhatsAppPublishingEngine Engine(Data.KinshoutDbContext db, RecordingPublisher publisher, DateTime utc, bool dryRun, bool enabled = true, bool traffic = false)
    {
        var settings = Options.Create(new WhatsAppPublishingSettings
        {
            Enabled = enabled,
            DryRun = dryRun,
            TrafficRedistributionPermitted = traffic,
            ApifyToken = traffic ? "test-token" : "",
            MinimumPublicationSpacingMinutes = 0,
        });
        return new WhatsAppPublishingEngine(
            db,
            settings,
            new FixedWeather(),
            new IdleTraffic(),
            publisher,
            new EditorialCandidateSource(db, settings),
            new EmptyKinshasaEventSource(),
            new FixedClock(utc),
            NullLogger<WhatsAppPublishingEngine>.Instance);
    }

    private static Discussion Story(Guid userId, Guid communityId, string topic, string title, DateTime published, bool blocked = false, string? provider = null, bool reviewed = true) =>
        new()
        {
            UserId = userId,
            CommunityId = communityId,
            TopicSlug = topic,
            Title = title,
            Body = title,
            Visibility = "public",
            PublicFeedBlocked = blocked,
            PublicFeedRuleVersion = reviewed ? 1 : 0,
            SourceProvider = provider,
            ExternalPublishedAt = provider is null ? null : published,
            CreatedAt = published,
            LikeCount = 5000,
        };

    private static List<WeatherHour> Hours()
    {
        (int hour, double temp, double apparent, int pop, double rain, int code)[] rows =
        [
            (6, 24.1, 29.2, 25, 0, 45), (7, 24.5, 29.5, 25, 0, 3), (8, 25.4, 30.2, 21, 0, 3),
            (9, 26.5, 31.2, 15, 0, 3), (10, 27.0, 31.3, 10, 0.10, 51), (11, 27.3, 32.2, 6, 0, 3),
            (12, 28.2, 33.1, 3, 0, 3), (13, 29.0, 33.7, 2, 0, 3), (14, 29.5, 33.9, 3, 0, 2),
            (15, 30.0, 34.3, 6, 0, 1), (16, 29.8, 33.8, 8, 0, 1), (17, 29.0, 33.1, 8, 0, 2),
            (18, 27.8, 32.6, 6, 0, 3), (19, 27.4, 32.6, 6, 0, 3), (20, 27.3, 32.6, 8, 0, 3),
            (21, 26.8, 31.8, 11, 0, 3), (22, 26.1, 30.8, 12, 0, 3), (23, 25.4, 30.4, 10, 0, 2),
        ];
        return rows.Select(r => new WeatherHour(new DateTime(2026, 11, 28, r.hour, 0, 0), r.temp, r.apparent, r.pop, r.rain, r.code, 4)).ToList();
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

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    private sealed class FixedWeather : IOpenMeteoForecastClient
    {
        public Task<WeatherDay?> GetKinshasaAsync(DateTime localDate, CancellationToken ct) =>
            Task.FromResult<WeatherDay?>(new WeatherDay(localDate.Date, 24, 30, 29, 34, Hours()));
    }

    private sealed class IdleTraffic : IApifyTrafficClient
    {
        public Task<TrafficMeasurement> MeasureAsync(DateTime observedAtUtc, CancellationToken ct) =>
            Task.FromResult(new TrafficMeasurement(null, []));
    }

    private sealed class RecordingPublisher : IWhatsAppChannelPublisher
    {
        public List<string> Texts { get; } = [];
        public Task<ChannelPublishResult> SendTextAsync(string destination, string body, CancellationToken ct)
        {
            Texts.Add(body);
            return Task.FromResult(new ChannelPublishResult(true, "msg", false, false, false, null));
        }
        public Task<ChannelPublishResult> SendPollAsync(string destination, string question, IReadOnlyList<string> options, CancellationToken ct) =>
            Task.FromResult(new ChannelPublishResult(false, null, true, false, false, "unsupported"));
        public Task<bool> MessageExistsAsync(string destination, string messageId, CancellationToken ct) =>
            Task.FromResult(false);
    }
}
