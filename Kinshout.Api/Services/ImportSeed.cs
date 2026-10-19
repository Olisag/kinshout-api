using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public static class ImportSeed
{
    public const string ImportUserEmail = "imports@kinshout.system";

    public static async Task<User> EnsureImportUserAsync(KinshoutDbContext db, CancellationToken ct = default)
    {
        var user = await StageImportUserAsync(db, ct);
        if (db.Entry(user).State == EntityState.Added)
            await db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>Finds the system import user, or adds it to the change tracker without saving.</summary>
    public static async Task<User> StageImportUserAsync(KinshoutDbContext db, CancellationToken ct = default)
    {
        var user = db.Users.Local.FirstOrDefault(u => u.Email == ImportUserEmail)
            ?? await db.Users.FirstOrDefaultAsync(u => u.Email == ImportUserEmail, ct);
        if (user is not null)
            return user;

        user = new User
        {
            Email = ImportUserEmail,
            DisplayName = "Kinshout",
        };
        db.Users.Add(user);
        return user;
    }
}
