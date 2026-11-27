using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Data;

public static partial class DbSchemaPatcher
{
    private static async Task EnsureDiscoverySchemaAsync(
        KinshoutDbContext db,
        System.Data.Common.DbConnection connection,
        bool sqlServer,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, sqlServer, "DiscoverySpends", ct))
        {
            if (sqlServer)
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE DiscoverySpends (
                        PeriodKey nvarchar(40) NOT NULL,
                        ResultsRequested int NOT NULL,
                        CONSTRAINT PK_DiscoverySpends PRIMARY KEY (PeriodKey)
                    )
                    """, ct);
            }
            else
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE DiscoverySpends (
                        PeriodKey TEXT NOT NULL CONSTRAINT PK_DiscoverySpends PRIMARY KEY,
                        ResultsRequested INTEGER NOT NULL
                    )
                    """, ct);
            }
        }

        if (await TableExistsAsync(connection, sqlServer, "DiscoverySourceStats", ct))
        {
            if (!await ColumnExistsAsync(connection, sqlServer, "DiscoverySourceStats", "Verified", ct))
            {
                var add = sqlServer
                    ? "ALTER TABLE DiscoverySourceStats ADD Verified bit NOT NULL CONSTRAINT DF_DiscoverySourceStats_Verified DEFAULT 0"
                    : "ALTER TABLE DiscoverySourceStats ADD COLUMN Verified INTEGER NOT NULL DEFAULT 0";
                await db.Database.ExecuteSqlRawAsync(add, ct);
            }

            return;
        }

        if (sqlServer)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE DiscoverySourceStats (
                    [Key] nvarchar(120) NOT NULL,
                    Platform nvarchar(40) NOT NULL,
                    Handle nvarchar(80) NOT NULL,
                    Checks int NOT NULL,
                    Kept int NOT NULL,
                    Imports int NOT NULL,
                    Verified bit NOT NULL CONSTRAINT DF_DiscoverySourceStats_Verified DEFAULT 0,
                    LastCheckedAt datetime2 NOT NULL,
                    CONSTRAINT PK_DiscoverySourceStats PRIMARY KEY ([Key])
                )
                """, ct);
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IX_DiscoverySourceStats_Platform ON DiscoverySourceStats (Platform)", ct);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE DiscoverySourceStats (
                    Key TEXT NOT NULL CONSTRAINT PK_DiscoverySourceStats PRIMARY KEY,
                    Platform TEXT NOT NULL,
                    Handle TEXT NOT NULL,
                    Checks INTEGER NOT NULL,
                    Kept INTEGER NOT NULL,
                    Imports INTEGER NOT NULL,
                    Verified INTEGER NOT NULL DEFAULT 0,
                    LastCheckedAt TEXT NOT NULL
                )
                """, ct);
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IX_DiscoverySourceStats_Platform ON DiscoverySourceStats (Platform)", ct);
        }
    }
}
