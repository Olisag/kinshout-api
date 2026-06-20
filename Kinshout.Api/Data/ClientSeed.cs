using Kinshout.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Data;

public static class ClientSeed
{
    public const string DefaultClientId = "kinshout-web";

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
}
