using System.Text.RegularExpressions;
using Kinshout.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IUsernameService
{
    string Normalize(string username);
    void ValidateFormat(string username);
    Task<bool> IsAvailableAsync(string username, Guid? excludeUserId = null, CancellationToken ct = default);
    Task<string> GenerateUniqueAsync(CancellationToken ct = default);
    Task EnsureAllUsersHaveUsernamesAsync(CancellationToken ct = default);
}

public partial class UsernameService(KinshoutDbContext db) : IUsernameService
{
    public const int MinLength = 2;
    public const int MaxLength = 20;

    private static readonly string[] Adjectives =
    [
        "calm", "brave", "quick", "happy", "lucky", "noble", "witty", "zesty",
        "bold", "keen", "warm", "cool", "wise", "fair", "kind", "neat",
    ];

    private static readonly string[] Nouns =
    [
        "panda", "lemur", "tiger", "koala", "eagle", "otter", "fox", "wolf",
        "bear", "lynx", "heron", "finch", "crane", "bison", "gecko", "mole",
    ];

    [GeneratedRegex("^[a-z][a-z0-9_]{2,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedUsernamePattern();

    public string Normalize(string username) =>
        username.Trim().ToLowerInvariant();

    /// <summary>
    /// Validates a user-chosen username. Auto-generated Reddit-style names are not validated here.
    /// </summary>
    public void ValidateFormat(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Le nom d'utilisateur est requis.");

        var trimmed = username.Trim();
        if (trimmed.Length < MinLength || trimmed.Length > MaxLength)
        {
            throw new ArgumentException(
                $"Le nom d'utilisateur doit contenir entre {MinLength} et {MaxLength} caractères.");
        }

        if (trimmed.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Le nom d'utilisateur ne peut pas contenir d'espaces.");
        }
    }

    public async Task<bool> IsAvailableAsync(
        string username,
        Guid? excludeUserId = null,
        CancellationToken ct = default)
    {
        ValidateFormat(username);
        return await IsAvailableNormalizedAsync(Normalize(username), excludeUserId, ct);
    }

    private async Task<bool> IsAvailableNormalizedAsync(
        string normalized,
        Guid? excludeUserId,
        CancellationToken ct)
    {
        var query = db.Users.AsNoTracking().Where(u => u.Username == normalized);
        if (excludeUserId.HasValue)
            query = query.Where(u => u.Id != excludeUserId.Value);

        return !await query.AnyAsync(ct);
    }

    public async Task<string> GenerateUniqueAsync(CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var candidate = BuildRandomUsername();
            if (GeneratedUsernamePattern().IsMatch(candidate)
                && await IsAvailableNormalizedAsync(candidate, excludeUserId: null, ct))
                return candidate;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = $"user_{Guid.NewGuid():N}"[..Math.Min(MaxLength, 19)];
            if (await IsAvailableNormalizedAsync(candidate, excludeUserId: null, ct))
                return candidate;
        }

        throw new InvalidOperationException("Impossible de générer un nom d'utilisateur unique.");
    }

    public async Task EnsureAllUsersHaveUsernamesAsync(CancellationToken ct = default)
    {
        var users = await db.Users
            .Where(u => u.Username == null || u.Username == "")
            .ToListAsync(ct);

        foreach (var user in users)
            user.Username = await GenerateUniqueAsync(ct);

        if (users.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private static string BuildRandomUsername()
    {
        var adjective = Adjectives[Random.Shared.Next(Adjectives.Length)];
        var noun = Nouns[Random.Shared.Next(Nouns.Length)];
        var suffix = Random.Shared.Next(1000, 10_000);
        var raw = $"{adjective}_{noun}{suffix}";
        return raw.Length <= MaxLength ? raw : raw[..MaxLength];
    }
}
