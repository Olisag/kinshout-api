using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public sealed class FeedImportDispatchService(
    KinshoutDbContext db,
    IHttpClientFactory http,
    IOptions<FeedImportDispatchSettings> settings,
    ILogger<FeedImportDispatchService> logger,
    TimeProvider? clock = null)
{
    public const string HttpClientName = "feed-import-dispatch";

    private DateTime UtcNow => (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    public async Task<int> DispatchDueAsync(CancellationToken ct = default)
    {
        var config = settings.Value;
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.GitHubToken))
            return 0;

        var started = 0;
        foreach (var slot in FeedImportClock.Due(UtcNow, config.CatchUpMinutes))
        {
            if (await db.FeedImportDispatches.AnyAsync(x => x.SlotKey == slot.SlotKey, ct))
                continue;

            db.FeedImportDispatches.Add(new FeedImportDispatch
            {
                SlotKey = slot.SlotKey,
                Workflow = slot.WorkflowFile,
                DispatchedAt = UtcNow,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                continue;
            }

            try
            {
                await StartWorkflowAsync(config, slot.WorkflowFile, ct);
                started++;
                logger.LogInformation("Started feed import {Workflow} for {Slot}.", slot.WorkflowFile, slot.SlotKey);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Feed import {Workflow} for {Slot} was not accepted. It can be tried again inside the catch-up window.", slot.WorkflowFile, slot.SlotKey);
                var claimed = await db.FeedImportDispatches.FindAsync([slot.SlotKey], ct);
                if (claimed is not null)
                    db.FeedImportDispatches.Remove(claimed);
                await db.SaveChangesAsync(ct);
            }
        }
        return started;
    }

    private async Task StartWorkflowAsync(FeedImportDispatchSettings config, string workflowFile, CancellationToken ct)
    {
        var repository = config.Repository.Trim().Trim('/');
        var client = http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"repos/{repository}/actions/workflows/{Uri.EscapeDataString(workflowFile)}/dispatches");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.GitHubToken.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("kinshout-api");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Content = JsonContent.Create(new Dictionary<string, string> { ["ref"] = config.Ref }, options: new JsonSerializerOptions());
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub workflow dispatch returned {(int)response.StatusCode}.");
    }
}
