using System.Net;
using Kinshout.Api.Configuration;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Tests;

public class FeedImportClockTests
{
    [Fact]
    public void MorningSlotsOpenOnTheKinshasaClockAndCloseAfterNinetyMinutes()
    {
        var eight = new DateTime(2026, 11, 26, 7, 0, 0, DateTimeKind.Utc);
        var stillOpen = eight.AddMinutes(89);
        var closed = eight.AddMinutes(90);

        Assert.Contains(FeedImportClock.Due(eight, 90), slot => slot.SlotKey == "tiktok:2026-11-26:08:00");
        Assert.Contains(FeedImportClock.Due(stillOpen, 90), slot => slot.SlotKey == "tiktok:2026-11-26:08:00");
        Assert.DoesNotContain(FeedImportClock.Due(closed, 90), slot => slot.SlotKey == "tiktok:2026-11-26:08:00");
        Assert.DoesNotContain(FeedImportClock.Due(eight, 90), slot => slot.WorkflowFile.Contains("schedule", StringComparison.Ordinal));
    }

    [Fact]
    public void MidnightChannelCheckUsesTheKinshasaDate()
    {
        var midnight = new DateTime(2026, 11, 25, 23, 0, 0, DateTimeKind.Utc);
        var due = FeedImportClock.Due(midnight, 90);

        Assert.Contains(due, slot => slot.SlotKey == "youtube-channels:2026-11-26:00:00"
            && slot.WorkflowFile == "external-discussion-importer-prod-youtube.yml");
    }

    [Fact]
    public async Task AClaimedSlotIsNotStartedAgainAndARejectedStartCanBeRetried()
    {
        await using var db = await TestDbFactory.CreateSqliteAsync();
        var clock = new FixedClock(new DateTime(2026, 11, 26, 15, 0, 0, DateTimeKind.Utc));
        var calls = 0;
        var handler = new StubHandler(request =>
        {
            calls++;
            Assert.Contains("external-discussion-importer-prod-schedule.yml", request.RequestUri?.AbsolutePath);
            var status = calls == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.NoContent;
            return new HttpResponseMessage(status);
        });
        var services = new ServiceCollection();
        services.AddHttpClient(FeedImportDispatchService.HttpClientName, client => client.BaseAddress = new Uri("https://api.github.com/"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var options = Options.Create(new FeedImportDispatchSettings { Enabled = true, GitHubToken = "test-token" });

        var service = new FeedImportDispatchService(db, factory, options, NullLogger<FeedImportDispatchService>.Instance, clock);
        Assert.Equal(0, await service.DispatchDueAsync());
        Assert.Empty(await db.FeedImportDispatches.ToListAsync());

        Assert.Equal(1, await service.DispatchDueAsync());
        Assert.Equal(1, await db.FeedImportDispatches.CountAsync(x => x.SlotKey == "facebook:2026-11-26:16:00"));
        Assert.Equal(0, await service.DispatchDueAsync());
        Assert.Equal(2, calls);
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
