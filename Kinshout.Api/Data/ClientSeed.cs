using System.Text.Json;
using Kinshout.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Data;

public static class ClientSeed
{
    public const string DefaultClientId = "kinshout-web";

    public static readonly string[] DefaultAllowedOrigins =
    [
        "https://kinshout.vercel.app",
        "https://*.vercel.app",
        "http://localhost:5173",
        "http://127.0.0.1:5173",
        "http://localhost:5280",
    ];

    public static async Task EnsureClientSecretAsync(
        KinshoutDbContext db,
        IPasswordHasher<ApiClient> passwordHasher,
        string? secret,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "ClientAuth:KinshoutWebSecret must be set on the server (never in the browser).");
        }

        var client = await db.ApiClients.FirstOrDefaultAsync(c => c.ClientId == DefaultClientId, ct);
        if (client is null)
            return;

        var needsHash = string.IsNullOrWhiteSpace(client.SecretHash)
            || client.SecretHash == "pending"
            || passwordHasher.VerifyHashedPassword(client, client.SecretHash, secret)
                == PasswordVerificationResult.Failed;

        if (needsHash)
        {
            client.SecretHash = passwordHasher.HashPassword(client, secret);
            await db.SaveChangesAsync(ct);
        }
    }

    public static async Task EnsureAllowedOriginsAsync(KinshoutDbContext db, CancellationToken ct = default)
    {
        var client = await db.ApiClients.FirstOrDefaultAsync(c => c.ClientId == DefaultClientId, ct);
        if (client is null)
            return;

        var current = JsonSerializer.Deserialize<List<string>>(client.AllowedOriginsJson) ?? [];
        var merged = current.ToList();
        var changed = false;

        foreach (var origin in DefaultAllowedOrigins)
        {
            if (merged.Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase)))
                continue;

            merged.Add(origin);
            changed = true;
        }

        if (!changed)
            return;

        client.AllowedOriginsJson = JsonSerializer.Serialize(merged);
        await db.SaveChangesAsync(ct);
    }
}
