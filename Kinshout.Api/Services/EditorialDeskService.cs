using System.Text.Json;
using System.Text.Json.Nodes;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public sealed record GrosPlanAssignment(Guid? DiscussionId, string? Window);

public sealed record RegistrySourceInput(
    string Name,
    string CommunitySlug,
    string? SearchQuery,
    IReadOnlyList<string>? Mentions,
    string? YoutubeChannelId,
    string? YoutubeHandle,
    string? Tiktok,
    string? Facebook,
    string? X,
    bool HitsOnly);

public interface IEditorialDeskService
{
    Task<GrosPlanAssignment> GetGrosPlanAsync(CancellationToken ct = default);
    Task<GrosPlanAssignment> SetGrosPlanAsync(Guid userId, Guid? discussionId, string? window, CancellationToken ct = default);
    Task<string> GetRegistryJsonAsync(CancellationToken ct = default);
    Task EnsureCanEditAsync(Guid userId, CancellationToken ct = default);
    Task SetSourceImportAsync(Guid userId, string name, bool import, CancellationToken ct = default);
    Task AddSourceAsync(Guid userId, RegistrySourceInput input, CancellationToken ct = default);
}

public class EditorialDeskService(KinshoutDbContext db) : IEditorialDeskService
{
    private const string GrosPlanKey = "gros-plan";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GrosPlanAssignment> GetGrosPlanAsync(CancellationToken ct = default)
    {
        var pin = await db.EditorialPins.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Key == GrosPlanKey, ct);
        if (pin?.DiscussionId is null || string.IsNullOrWhiteSpace(pin.Window))
            return new GrosPlanAssignment(null, null);

        return new GrosPlanAssignment(pin.DiscussionId, pin.Window);
    }

    public async Task<GrosPlanAssignment> SetGrosPlanAsync(
        Guid userId,
        Guid? discussionId,
        string? window,
        CancellationToken ct = default)
    {
        await EnsureGeneralModeratorAsync(userId, ct);

        if (discussionId is null)
        {
            await SavePinAsync(null, null, ct);
            return new GrosPlanAssignment(null, null);
        }

        var normalizedWindow = window?.Trim().ToLowerInvariant();
        if (normalizedWindow is not ("morning" or "midday" or "evening"))
            throw new ArgumentException("La fenêtre doit être morning, midday ou evening.");

        var exists = await db.Discussions.AsNoTracking().AnyAsync(d => d.Id == discussionId, ct);
        if (!exists)
            throw new KeyNotFoundException("Discussion introuvable.");

        await SavePinAsync(discussionId, normalizedWindow, ct);
        return new GrosPlanAssignment(discussionId, normalizedWindow);
    }

    public async Task<string> GetRegistryJsonAsync(CancellationToken ct = default)
    {
        var sources = await LoadMergedSourcesAsync(ct);
        return JsonSerializer.Serialize(new { sources }, JsonOptions);
    }

    public Task EnsureCanEditAsync(Guid userId, CancellationToken ct = default) =>
        EnsureGeneralModeratorAsync(userId, ct);

    public async Task SetSourceImportAsync(Guid userId, string name, bool import, CancellationToken ct = default)
    {
        await EnsureGeneralModeratorAsync(userId, ct);
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Nom requis.");

        var sources = await LoadMergedSourcesAsync(ct);
        var match = sources.FirstOrDefault(source =>
            string.Equals(source["name"]?.GetValue<string>(), trimmed, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("Source introuvable.");

        var canonicalName = match["name"]!.GetValue<string>();
        var record = await db.RegistrySourceRecords.FirstOrDefaultAsync(r => r.Name == canonicalName, ct);
        if (record is null)
        {
            db.RegistrySourceRecords.Add(new RegistrySourceRecord
            {
                Name = canonicalName,
                Import = import,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            record.Import = import;
            record.UpdatedAt = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(record.DefinitionJson))
            {
                var definition = JsonNode.Parse(record.DefinitionJson) as JsonObject
                    ?? throw new InvalidOperationException("Définition de source illisible.");
                definition["import"] = import;
                record.DefinitionJson = definition.ToJsonString(JsonOptions);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task AddSourceAsync(Guid userId, RegistrySourceInput input, CancellationToken ct = default)
    {
        await EnsureGeneralModeratorAsync(userId, ct);
        var name = input.Name.Trim();
        if (name.Length is 0 or > 160)
            throw new ArgumentException("Nom requis.");

        var community = input.CommunitySlug.Trim().ToLowerInvariant();
        if (!ReadOnlyCommunityCatalog.IsKnownLaunchRoom(community))
            throw new ArgumentException("Communauté inconnue.");

        var sources = await LoadMergedSourcesAsync(ct);
        if (sources.Any(source =>
            string.Equals(source["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Cette source existe déjà.");
        }

        var channelId = Clean(input.YoutubeChannelId);
        if (channelId is not null && (channelId.Length != 24 || !channelId.StartsWith("UC", StringComparison.Ordinal)))
            throw new ArgumentException("Identifiant de chaîne YouTube invalide.");

        var mentions = (input.Mentions ?? [])
            .Select(term => term.Trim())
            .Where(term => term.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var definition = new JsonObject
        {
            ["name"] = name,
            ["priority"] = "P0",
            ["moderation"] = "Publish",
            ["communitySlug"] = community,
            ["import"] = true,
            ["webSub"] = false,
            ["backfill"] = false,
            ["hitsOnly"] = input.HitsOnly && channelId is not null,
            ["youtubeChannelId"] = channelId,
            ["youtubeHandle"] = Clean(input.YoutubeHandle),
            ["tiktok"] = Clean(input.Tiktok)?.TrimStart('@'),
            ["facebook"] = Clean(input.Facebook),
            ["x"] = Clean(input.X)?.TrimStart('@'),
            ["searchQuery"] = Clean(input.SearchQuery),
            ["mentions"] = new JsonArray(mentions.Select(term => JsonValue.Create(term)).ToArray()),
            ["notes"] = "Added from the editorial desk.",
        };

        db.RegistrySourceRecords.Add(new RegistrySourceRecord
        {
            Name = name,
            Import = true,
            DefinitionJson = definition.ToJsonString(JsonOptions),
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task SavePinAsync(Guid? discussionId, string? window, CancellationToken ct)
    {
        var pin = await db.EditorialPins.FirstOrDefaultAsync(p => p.Key == GrosPlanKey, ct);
        if (pin is null)
        {
            db.EditorialPins.Add(new EditorialPin
            {
                Key = GrosPlanKey,
                DiscussionId = discussionId,
                Window = window,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            pin.DiscussionId = discussionId;
            pin.Window = window;
            pin.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureGeneralModeratorAsync(Guid userId, CancellationToken ct)
    {
        var community = await db.Communities.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Slug == CommunityDefaults.GeneralSlug, ct)
            ?? throw new UnauthorizedAccessException("k/general est introuvable.");
        var membership = await db.CommunityMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.CommunityId == community.Id && m.UserId == userId, ct);
        if (!CommunityAccessHelper.CanModerate(community, membership, userId))
            throw new UnauthorizedAccessException("Seuls les modérateurs de k/general peuvent modifier la rédaction.");
    }

    private async Task<List<JsonObject>> LoadMergedSourcesAsync(CancellationToken ct)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "source-registry.json");
        var sources = new List<JsonObject>();
        if (File.Exists(path))
        {
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject;
            if (document?["sources"] is JsonArray fileSources)
            {
                foreach (var node in fileSources)
                {
                    if (node is JsonObject source)
                        sources.Add(source);
                }
            }
        }

        var records = await db.RegistrySourceRecords.AsNoTracking().ToListAsync(ct);
        foreach (var record in records.Where(r => string.IsNullOrWhiteSpace(r.DefinitionJson)))
        {
            var match = sources.FirstOrDefault(source =>
                string.Equals(source["name"]?.GetValue<string>(), record.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                match["import"] = record.Import;
        }

        var fileNames = sources
            .Select(source => source["name"]?.GetValue<string>() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.Where(r => !string.IsNullOrWhiteSpace(r.DefinitionJson)))
        {
            if (fileNames.Contains(record.Name))
            {
                var match = sources.FirstOrDefault(source =>
                    string.Equals(source["name"]?.GetValue<string>(), record.Name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    match["import"] = record.Import;
                continue;
            }

            if (JsonNode.Parse(record.DefinitionJson!) is JsonObject added)
            {
                added["import"] = record.Import;
                sources.Add(added);
            }
        }

        return sources;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
