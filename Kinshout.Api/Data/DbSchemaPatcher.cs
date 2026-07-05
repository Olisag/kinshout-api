using System.Data;
using System.Data.Common;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Data;

public static class DbSchemaPatcher
{
    public static async Task ApplyAsync(KinshoutDbContext db, CancellationToken ct = default)
    {
        if (db.Database.IsSqlite())
            await ApplySqliteAsync(db, ct);
        else if (db.Database.IsSqlServer())
            await ApplySqlServerAsync(db, ct);
    }

    private static async Task ApplySqlServerAsync(KinshoutDbContext db, CancellationToken ct)
    {
        var connection = await OpenConnectionAsync(db, ct);
        await RemoveUserUsernameSchemaAsync(db, connection, sqlServer: true, ct);
        await EnsureSearchQueryStatsSchemaAsync(db, connection, sqlServer: true, ct);
        await EnsureDiscussionEngagementSchemaAsync(db, connection, sqlServer: true, ct);
        await EnsureExternalAdvertSourceSchemaAsync(db, connection, sqlServer: true, ct);
        await EnsureExternalDiscussionSourceSchemaAsync(db, connection, sqlServer: true, ct);
        await EnsureImportWatermarkSchemaAsync(db, connection, sqlServer: true, ct);
        await NormalizeExternalDiscussionViewCountsAsync(db, ct);
        if (await ColumnExistsAsync(connection, sqlServer: true, "Adverts", "DetailsJson", ct))
            await EnsureAdvertJsonColumnDefaultsAsync(db, ct);
    }

    private static async Task ApplySqliteAsync(KinshoutDbContext db, CancellationToken ct)
    {
        var connection = await OpenConnectionAsync(db, ct);

        if (!await ColumnExistsAsync(connection, sqlServer: false, "Users", "WhatsAppNumber", ct))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Users ADD COLUMN WhatsAppNumber TEXT", ct);

        if (!await ColumnExistsAsync(connection, sqlServer: false, "Users", "DisplayPreference", ct))
        {
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE Users ADD COLUMN DisplayPreference TEXT NOT NULL DEFAULT '{DisplayPreferenceMode.Clair}'",
                ct);
        }

        if (!await ColumnExistsAsync(connection, sqlServer: false, "Users", "IsProfilePublic", ct))
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

        await RemoveUserUsernameSchemaAsync(db, connection, sqlServer: false, ct);
        await EnsureSearchQueryStatsSchemaAsync(db, connection, sqlServer: false, ct);

        if (!await ColumnExistsAsync(connection, sqlServer: false, "Adverts", "ImageUrlsJson", ct))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Adverts ADD COLUMN ImageUrlsJson TEXT NOT NULL DEFAULT '[]'", ct);

        if (!await ColumnExistsAsync(connection, sqlServer: false, "Adverts", "ResumeUrl", ct))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Adverts ADD COLUMN ResumeUrl TEXT", ct);

        if (!await ColumnExistsAsync(connection, sqlServer: false, "Adverts", "ViewCount", ct))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Adverts ADD COLUMN ViewCount INTEGER NOT NULL DEFAULT 0", ct);

        if (!await ColumnExistsAsync(connection, sqlServer: false, "Adverts", "LikeCount", ct))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Adverts ADD COLUMN LikeCount INTEGER NOT NULL DEFAULT 0", ct);

        await EnsureDiscussionEngagementSchemaAsync(db, connection, sqlServer: false, ct);

        await EnsureExternalAdvertSourceSchemaAsync(db, connection, sqlServer: false, ct);
        await EnsureExternalDiscussionSourceSchemaAsync(db, connection, sqlServer: false, ct);
        await EnsureImportWatermarkSchemaAsync(db, connection, sqlServer: false, ct);
        await NormalizeExternalDiscussionViewCountsAsync(db, ct);
        if (await ColumnExistsAsync(connection, sqlServer: false, "Adverts", "DetailsJson", ct))
            await EnsureAdvertJsonColumnDefaultsAsync(db, ct);

        if (await TableExistsAsync(connection, sqlServer: false, "SavedAdverts", ct))
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

        if (await ColumnExistsAsync(connection, sqlServer: false, "Adverts", "ImageUrl", ct))
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

        if (await ColumnExistsAsync(connection, sqlServer: false, "Users", "Phone", ct))
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

        if (!await TableExistsAsync(connection, sqlServer: false, "SavedAdverts", ct))
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

    private static async Task EnsureDiscussionEngagementSchemaAsync(
        KinshoutDbContext db,
        DbConnection connection,
        bool sqlServer,
        CancellationToken ct)
    {
        if (!await ColumnExistsAsync(connection, sqlServer, "Discussions", "ReplyCount", ct))
        {
            if (sqlServer)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE Discussions ADD ReplyCount int NOT NULL CONSTRAINT DF_Discussions_ReplyCount DEFAULT 0",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE d
                    SET ReplyCount = counts.Total
                    FROM Discussions d
                    INNER JOIN (
                        SELECT DiscussionId, COUNT(*) AS Total
                        FROM DiscussionReplies
                        GROUP BY DiscussionId
                    ) counts ON counts.DiscussionId = d.Id
                    """, ct);
            }
            else
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE Discussions ADD COLUMN ReplyCount INTEGER NOT NULL DEFAULT 0", ct);
                await db.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE Discussions
                    SET ReplyCount = (
                        SELECT COUNT(*)
                        FROM DiscussionReplies
                        WHERE DiscussionReplies.DiscussionId = Discussions.Id
                    )
                    """, ct);
            }
        }

        if (!await ColumnExistsAsync(connection, sqlServer, "Discussions", "LikeCount", ct))
        {
            var likeCountSql = sqlServer
                ? "ALTER TABLE Discussions ADD LikeCount int NOT NULL CONSTRAINT DF_Discussions_LikeCount DEFAULT 0"
                : "ALTER TABLE Discussions ADD COLUMN LikeCount INTEGER NOT NULL DEFAULT 0";
            await db.Database.ExecuteSqlRawAsync(likeCountSql, ct);
        }

        if (!await ColumnExistsAsync(connection, sqlServer, "Discussions", "ViewCount", ct))
        {
            var viewCountSql = sqlServer
                ? "ALTER TABLE Discussions ADD ViewCount int NOT NULL CONSTRAINT DF_Discussions_ViewCount DEFAULT 0"
                : "ALTER TABLE Discussions ADD COLUMN ViewCount INTEGER NOT NULL DEFAULT 0";
            await db.Database.ExecuteSqlRawAsync(viewCountSql, ct);
        }

        if (!await TableExistsAsync(connection, sqlServer, "LikedDiscussions", ct))
        {
            if (sqlServer)
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE LikedDiscussions (
                        UserId uniqueidentifier NOT NULL,
                        DiscussionId uniqueidentifier NOT NULL,
                        LikedAt datetime2 NOT NULL,
                        CONSTRAINT PK_LikedDiscussions PRIMARY KEY (UserId, DiscussionId),
                        CONSTRAINT FK_LikedDiscussions_Discussions_DiscussionId FOREIGN KEY (DiscussionId) REFERENCES Discussions(Id) ON DELETE CASCADE,
                        CONSTRAINT FK_LikedDiscussions_Users_UserId FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
                    )
                    """, ct);
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE INDEX IX_LikedDiscussions_DiscussionId ON LikedDiscussions (DiscussionId)", ct);
            }
            else
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE LikedDiscussions (
                        UserId TEXT NOT NULL,
                        DiscussionId TEXT NOT NULL,
                        LikedAt TEXT NOT NULL,
                        PRIMARY KEY (UserId, DiscussionId),
                        FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE,
                        FOREIGN KEY (DiscussionId) REFERENCES Discussions(Id) ON DELETE CASCADE
                    )
                    """, ct);
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE INDEX IX_LikedDiscussions_DiscussionId ON LikedDiscussions (DiscussionId)", ct);
            }
        }

        if (await TableExistsAsync(connection, sqlServer, "LikedDiscussions", ct))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE Discussions
                SET LikeCount = (
                    SELECT COUNT(*)
                    FROM LikedDiscussions
                    WHERE LikedDiscussions.DiscussionId = Discussions.Id
                )
                """, ct);
        }
    }

    private static async Task EnsureExternalAdvertSourceSchemaAsync(
        KinshoutDbContext db,
        DbConnection connection,
        bool sqlServer,
        CancellationToken ct)
    {
        var columns = new (string Name, string SqlServerType, string SqliteType)[]
        {
            ("SourceProvider", "nvarchar(64) NULL", "TEXT"),
            ("SourceProviderName", "nvarchar(120) NULL", "TEXT"),
            ("SourceExternalId", "nvarchar(128) NULL", "TEXT"),
            ("SourceExternalUrl", "nvarchar(2048) NULL", "TEXT"),
            ("SourceImportedAt", "datetime2 NULL", "TEXT"),
            ("SourceLastSeenAt", "datetime2 NULL", "TEXT"),
            ("SourceFirstSeenAt", "datetime2 NULL", "TEXT"),
            ("SubcategorySlug", "nvarchar(80) NULL", "TEXT"),
            ("DetailsJson", "nvarchar(max) NULL", "TEXT"),
            ("ContactJson", "nvarchar(max) NULL", "TEXT"),
            ("DuplicateGroupId", "nvarchar(128) NULL", "TEXT"),
            ("ExternalPublishedAt", "datetime2 NULL", "TEXT"),
        };

        foreach (var (name, sqlServerType, sqliteType) in columns)
        {
            if (await ColumnExistsAsync(connection, sqlServer, "Adverts", name, ct))
                continue;

            var sql = sqlServer
                ? $"ALTER TABLE Adverts ADD {name} {sqlServerType}"
                : $"ALTER TABLE Adverts ADD COLUMN {name} {sqliteType}";
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken: ct);
        }

        if (!await IndexExistsAsync(connection, sqlServer, "IX_Adverts_SourceProvider_SourceExternalId", ct))
        {
            var indexSql = sqlServer
                ? """
                  CREATE UNIQUE INDEX IX_Adverts_SourceProvider_SourceExternalId
                  ON Adverts (SourceProvider, SourceExternalId)
                  WHERE SourceProvider IS NOT NULL AND SourceExternalId IS NOT NULL
                  """
                : """
                  CREATE UNIQUE INDEX IX_Adverts_SourceProvider_SourceExternalId
                  ON Adverts (SourceProvider, SourceExternalId)
                  WHERE SourceProvider IS NOT NULL AND SourceExternalId IS NOT NULL
                  """;
            await db.Database.ExecuteSqlRawAsync(indexSql, cancellationToken: ct);
        }
    }

    private static async Task EnsureExternalDiscussionSourceSchemaAsync(
        KinshoutDbContext db,
        DbConnection connection,
        bool sqlServer,
        CancellationToken ct)
    {
        var columns = new (string Name, string SqlServerType, string SqliteType)[]
        {
            ("SourceProvider", "nvarchar(64) NULL", "TEXT"),
            ("SourceProviderName", "nvarchar(120) NULL", "TEXT"),
            ("SourceExternalId", "nvarchar(128) NULL", "TEXT"),
            ("SourceExternalUrl", "nvarchar(2048) NULL", "TEXT"),
            ("SourceImportedAt", "datetime2 NULL", "TEXT"),
            ("SourceLastSeenAt", "datetime2 NULL", "TEXT"),
            ("SourceFirstSeenAt", "datetime2 NULL", "TEXT"),
            ("SourceOriginalAuthor", "nvarchar(200) NULL", "TEXT"),
            ("SourceEngagementScore", "int NULL", "INTEGER"),
            ("ExternalPublishedAt", "datetime2 NULL", "TEXT"),
            ("SourceRawBody", "nvarchar(max) NULL", "TEXT"),
        };

        foreach (var (name, sqlServerType, sqliteType) in columns)
        {
            if (await ColumnExistsAsync(connection, sqlServer, "Discussions", name, ct))
                continue;

            var sql = sqlServer
                ? $"ALTER TABLE Discussions ADD {name} {sqlServerType}"
                : $"ALTER TABLE Discussions ADD COLUMN {name} {sqliteType}";
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken: ct);
        }

        if (!await IndexExistsAsync(connection, sqlServer, "IX_Discussions_SourceProvider_SourceExternalId", ct))
        {
            var indexSql = """
                  CREATE UNIQUE INDEX IX_Discussions_SourceProvider_SourceExternalId
                  ON Discussions (SourceProvider, SourceExternalId)
                  WHERE SourceProvider IS NOT NULL AND SourceExternalId IS NOT NULL
                  """;
            await db.Database.ExecuteSqlRawAsync(indexSql, cancellationToken: ct);
        }
    }

    private static async Task EnsureImportWatermarkSchemaAsync(
        KinshoutDbContext db,
        DbConnection connection,
        bool sqlServer,
        CancellationToken ct)
    {
        if (await TableExistsAsync(connection, sqlServer, "ImportWatermarks", ct))
            return;

        if (sqlServer)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE ImportWatermarks (
                    Id uniqueidentifier NOT NULL,
                    ImportKind nvarchar(32) NOT NULL,
                    Provider nvarchar(64) NOT NULL,
                    LastRunAtUtc datetime2 NOT NULL,
                    CONSTRAINT PK_ImportWatermarks PRIMARY KEY (Id)
                )
                """, ct);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE ImportWatermarks (
                    Id TEXT NOT NULL PRIMARY KEY,
                    ImportKind TEXT NOT NULL,
                    Provider TEXT NOT NULL,
                    LastRunAtUtc TEXT NOT NULL
                )
                """, ct);
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE UNIQUE INDEX IX_ImportWatermarks_ImportKind_Provider
            ON ImportWatermarks (ImportKind, Provider)
            """, ct);
    }

    private static async Task NormalizeExternalDiscussionViewCountsAsync(KinshoutDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE Discussions
            SET ViewCount = CASE
                WHEN SourceEngagementScore IS NOT NULL AND ViewCount >= SourceEngagementScore
                THEN ViewCount - SourceEngagementScore
                ELSE ViewCount
            END
            WHERE SourceProvider IS NOT NULL
              AND SourceProvider <> 'kinshout'
              AND SourceEngagementScore IS NOT NULL
              AND ViewCount > 0
            """, ct);
    }

    private static async Task EnsureAdvertJsonColumnDefaultsAsync(
        KinshoutDbContext db,
        CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE Adverts SET DetailsJson = '{}' WHERE DetailsJson IS NULL
            """,
            ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE Adverts SET ContactJson = '{}' WHERE ContactJson IS NULL
            """,
            ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE Adverts SET TagsJson = '[]' WHERE TagsJson IS NULL OR TagsJson = ''
            """,
            ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE Adverts SET ImageUrlsJson = '[]' WHERE ImageUrlsJson IS NULL OR ImageUrlsJson = ''
            """,
            ct);
    }

    private static async Task EnsureSearchQueryStatsSchemaAsync(
        KinshoutDbContext db,
        DbConnection connection,
        bool sqlServer,
        CancellationToken ct)
    {
        if (await TableExistsAsync(connection, sqlServer, "SearchQueryStats", ct))
            return;

        if (sqlServer)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE SearchQueryStats (
                    Id uniqueidentifier NOT NULL,
                    NormalizedQuery nvarchar(200) NOT NULL,
                    DisplayQuery nvarchar(200) NOT NULL,
                    SearchCount int NOT NULL,
                    LastSearchedAt datetime2 NOT NULL,
                    CONSTRAINT PK_SearchQueryStats PRIMARY KEY (Id)
                )
                """,
                cancellationToken: ct);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE SearchQueryStats (
                    Id TEXT NOT NULL,
                    NormalizedQuery TEXT NOT NULL,
                    DisplayQuery TEXT NOT NULL,
                    SearchCount INTEGER NOT NULL,
                    LastSearchedAt TEXT NOT NULL,
                    PRIMARY KEY (Id)
                )
                """,
                cancellationToken: ct);
        }

        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IX_SearchQueryStats_NormalizedQuery ON SearchQueryStats (NormalizedQuery)",
            cancellationToken: ct);
    }

    private static async Task RemoveUserUsernameSchemaAsync(
        KinshoutDbContext db,
        DbConnection connection,
        bool sqlServer,
        CancellationToken ct)
    {
        if (!await ColumnExistsAsync(connection, sqlServer, "Users", "Username", ct))
            return;

        if (await IndexExistsAsync(connection, sqlServer, "IX_Users_Username", ct))
        {
            await db.Database.ExecuteSqlRawAsync(
                sqlServer
                    ? "DROP INDEX IX_Users_Username ON Users"
                    : "DROP INDEX IX_Users_Username",
                ct);
        }

        await db.Database.ExecuteSqlRawAsync(
            sqlServer
                ? "ALTER TABLE Users DROP COLUMN Username"
                : "ALTER TABLE Users DROP COLUMN Username",
            ct);
    }

    private static async Task<bool> IndexExistsAsync(
        DbConnection connection,
        bool sqlServer,
        string indexName,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        if (sqlServer)
        {
            cmd.CommandText = """
                SELECT 1
                FROM sys.indexes
                WHERE name = @name
                """;
            AddParameter(cmd, "@name", indexName);
        }
        else
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name=$name";
            AddParameter(cmd, "$name", indexName);
        }

        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<DbConnection> OpenConnectionAsync(KinshoutDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        bool sqlServer,
        string table,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        if (sqlServer)
        {
            cmd.CommandText = """
                SELECT 1
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_NAME = @table
                """;
            AddParameter(cmd, "@table", table);
        }
        else
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name";
            AddParameter(cmd, "$name", table);
        }

        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> ColumnExistsAsync(
        DbConnection connection,
        bool sqlServer,
        string table,
        string column,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        if (sqlServer)
        {
            cmd.CommandText = """
                SELECT 1
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = @table AND COLUMN_NAME = @column
                """;
            AddParameter(cmd, "@table", table);
            AddParameter(cmd, "@column", column);
            return await cmd.ExecuteScalarAsync(ct) is not null;
        }

        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var param = cmd.CreateParameter();
        param.ParameterName = name;
        param.Value = value;
        cmd.Parameters.Add(param);
    }
}
