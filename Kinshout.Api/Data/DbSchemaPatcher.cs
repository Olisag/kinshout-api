using System.Data;
using Kinshout.Api.Models;
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

        if (!await ColumnExistsAsync(connection, "Users", "DisplayPreference", ct))
        {
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE Users ADD COLUMN DisplayPreference TEXT NOT NULL DEFAULT '{DisplayPreferenceMode.Clair}'",
                ct);
        }

        if (!await ColumnExistsAsync(connection, "Users", "IsProfilePublic", ct))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Users ADD COLUMN IsProfilePublic INTEGER NOT NULL DEFAULT 1",
                ct);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE Users SET IsProfilePublic = 1 WHERE IsProfilePublic = 0",
                ct);
        }

        if (!await ColumnExistsAsync(connection, "Adverts", "ImageUrlsJson", ct))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Adverts ADD COLUMN ImageUrlsJson TEXT NOT NULL DEFAULT '[]'", ct);

        if (!await ColumnExistsAsync(connection, "Adverts", "ResumeUrl", ct))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Adverts ADD COLUMN ResumeUrl TEXT", ct);

        if (!await ColumnExistsAsync(connection, "Adverts", "ViewCount", ct))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Adverts ADD COLUMN ViewCount INTEGER NOT NULL DEFAULT 0", ct);

        if (!await ColumnExistsAsync(connection, "Adverts", "LikeCount", ct))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Adverts ADD COLUMN LikeCount INTEGER NOT NULL DEFAULT 0", ct);

        if (await TableExistsAsync(connection, "SavedAdverts", ct))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE Adverts
                SET LikeCount = (
                    SELECT COUNT(*)
                    FROM SavedAdverts
                    WHERE SavedAdverts.AdvertId = Adverts.Id
                )
                """, ct);
        }

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

        if (!await TableExistsAsync(connection, "SavedAdverts", ct))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE SavedAdverts (
                    UserId TEXT NOT NULL,
                    AdvertId TEXT NOT NULL,
                    SavedAt TEXT NOT NULL,
                    PRIMARY KEY (UserId, AdvertId),
                    FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE,
                    FOREIGN KEY (AdvertId) REFERENCES Adverts(Id) ON DELETE CASCADE
                )
                """, ct);
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IX_SavedAdverts_AdvertId ON SavedAdverts (AdvertId)", ct);
        }
    }

    private static async Task<bool> TableExistsAsync(
        System.Data.Common.DbConnection connection,
        string table,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name";
        var param = cmd.CreateParameter();
        param.ParameterName = "$name";
        param.Value = table;
        cmd.Parameters.Add(param);
        return await cmd.ExecuteScalarAsync(ct) is not null;
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
