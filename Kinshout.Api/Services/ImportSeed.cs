using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public static class ImportSeed
{
    public const string ImportUserEmail = "imports@kinshout.system";

    public static async Task<User> EnsureImportUserAsync(KinshoutDbContext db, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == ImportUserEmail, ct);
        if (user is not null)
            return user;

        user = new User
        {
            Email = ImportUserEmail,
            DisplayName = "Kinshout",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }
}
