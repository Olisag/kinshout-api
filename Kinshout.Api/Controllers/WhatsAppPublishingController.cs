using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Kinshout.Api.Services.WhatsApp;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/operations/whatsapp-channel")]
public sealed class WhatsAppPublishingController(
    WhatsAppPublishingEngine engine,
    KinshoutDbContext db,
    IOptions<WhatsAppPublishingSettings> settings,
    IOptions<ImportSettings> importSettings) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized();
        var config = settings.Value;
        var recent = await db.WhatsAppPublications
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(20)
            .Select(x => new { x.Slot, x.LocalDate, x.Status, x.LastError, x.ProviderMessageId })
            .ToListAsync(ct);
        return Ok(new
        {
            config.Enabled,
            config.DryRun,
            destination = WhatsAppPublishingEngine.Destination(config),
            paused = await db.WhatsAppChannelControls.Where(x => x.Id == 1).Select(x => x.Paused).FirstOrDefaultAsync(ct),
            recent,
        });
    }

    [HttpGet("schedule")]
    public IActionResult Schedule()
    {
        if (!IsAuthorized())
            return Unauthorized();
        var config = settings.Value;
        var zone = FeedImportClock.Kinshasa();
        var rows = WhatsAppSchedule.All.Select(slot =>
        {
            var sample = DateTime.SpecifyKind(new DateTime(2026, 11, 30).Add(slot.LocalTime.ToTimeSpan()), DateTimeKind.Unspecified);
            var utc = TimeZoneInfo.ConvertTimeToUtc(sample, zone);
            return new
            {
                slot.Id,
                slot.Category,
                slot.Purpose,
                local = slot.LocalTime.ToString("HH:mm"),
                utc = utc.ToString("HH:mm"),
                days = slot.Days?.Select(d => d.ToString()).ToArray() ?? ["every day"],
                enabled = WhatsAppSchedule.IsEnabled(slot, config),
                slot.GraceMinutes,
            };
        });
        return Ok(rows);
    }

    [HttpGet("preview/weather")]
    public async Task<IActionResult> PreviewWeather(CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized();
        var body = await engine.PreviewWeatherAsync(ct);
        return body is null ? StatusCode(503, new { error = "Prévision indisponible." }) : Ok(new { body });
    }

    [HttpPost("slots/{slotId}")]
    public async Task<IActionResult> Publish(string slotId, CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized();
        if (!settings.Value.Enabled)
            return Conflict(new { error = "La publication WhatsApp est arrêtée dans la configuration." });
        var row = await engine.PublishSlotAsync(slotId, ct);
        return row is null ? NotFound() : Ok(new { row.Id, row.Status, row.Body, row.LastError });
    }

    [HttpPost("pause")]
    public Task<IActionResult> Pause(CancellationToken ct) => SetPaused(true, ct);

    [HttpPost("resume")]
    public Task<IActionResult> Resume(CancellationToken ct) => SetPaused(false, ct);

    [HttpGet("history")]
    public async Task<IActionResult> History(CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized();
        var rows = await db.WhatsAppPublications
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(50)
            .Select(x => new
            {
                x.Id,
                x.Slot,
                x.Category,
                x.LocalDate,
                x.Status,
                x.LastError,
                x.ProviderName,
                x.ProviderMessageId,
                x.EstimatedCostUsd,
                x.CreatedAtUtc,
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    private async Task<IActionResult> SetPaused(bool paused, CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized();
        var control = await db.WhatsAppChannelControls.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (control is null)
        {
            control = new WhatsAppChannelControl { Id = 1, Paused = paused };
            db.WhatsAppChannelControls.Add(control);
        }
        else
            control.Paused = paused;
        await db.SaveChangesAsync(ct);
        return Ok(new { paused });
    }

    private bool IsAuthorized()
    {
        if (!Request.Headers.TryGetValue("X-Kinshout-Import-Key", out var provided))
            return false;
        var value = provided.ToString();
        if (value.Length == 0)
            return false;
        var admin = settings.Value.AdminKey;
        var import = importSettings.Value.SecretKey;
        return (!string.IsNullOrWhiteSpace(admin) && string.Equals(value, admin, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(import) && string.Equals(value, import, StringComparison.Ordinal));
    }
}
