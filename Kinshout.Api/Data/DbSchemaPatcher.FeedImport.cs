using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Data;

public static partial class DbSchemaPatcher
{
    private static async Task EnsureFeedImportDispatchSchemaAsync(
        KinshoutDbContext db, System.Data.Common.DbConnection connection, bool sqlServer, CancellationToken ct)
    {
        if (await TableExistsAsync(connection, sqlServer, "FeedImportDispatches", ct))
            return;

        var key = sqlServer ? "nvarchar(80)" : "TEXT";
        var workflow = sqlServer ? "nvarchar(160)" : "TEXT";
        var date = sqlServer ? "datetime2" : "TEXT";
        await db.Database.ExecuteSqlRawAsync($"""
            CREATE TABLE FeedImportDispatches (
                SlotKey {key} NOT NULL PRIMARY KEY,
                Workflow {workflow} NOT NULL,
                DispatchedAt {date} NOT NULL
            );
            """, ct);
    }
}
