using System.Data;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Data;

public static class DbSchemaPatcher
{
    public static async Task ApplyAsync(KinshoutDbContext db, CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        if (!await ColumnExistsAsync(connection, "Users", "WhatsAppNumber", ct))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Users ADD COLUMN WhatsAppNumber TEXT", ct);

        if (!await ColumnExistsAsync(connection, "Adverts", "ImageUrlsJson", ct))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Adverts ADD COLUMN ImageUrlsJson TEXT NOT NULL DEFAULT '[]'", ct);

        if (!await ColumnExistsAsync(connection, "Adverts", "ResumeUrl", ct))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Adverts ADD COLUMN ResumeUrl TEXT", ct);

        if (await ColumnExistsAsync(connection, "Adverts", "ImageUrl", ct))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE Adverts
                SET ImageUrlsJson = json_array(ImageUrl)
                WHERE (ImageUrlsJson IS NULL OR ImageUrlsJson = '[]' OR ImageUrlsJson = '')
                  AND ImageUrl IS NOT NULL
                  AND TRIM(ImageUrl) != ''
                """, ct);
        }

        if (await ColumnExistsAsync(connection, "Users", "Phone", ct))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE Users
                SET WhatsAppNumber = Phone
                WHERE (WhatsAppNumber IS NULL OR TRIM(WhatsAppNumber) = '')
                  AND Phone IS NOT NULL
                  AND TRIM(Phone) != ''
                """, ct);
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        System.Data.Common.DbConnection connection,
        string table,
        string column,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
