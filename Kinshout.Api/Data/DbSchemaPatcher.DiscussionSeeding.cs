using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Data;

public static partial class DbSchemaPatcher
{
    private static async Task EnsureDiscussionSeedingSchemaAsync(
        KinshoutDbContext db, DbConnection connection, bool sqlServer, CancellationToken ct)
    {
        foreach (var (table, sql) in new[]
        {
            ("DiscussionSeedingControls", DiscussionSeedingSchema.CreateControl(sqlServer)),
            ("DiscussionSeedRuns", DiscussionSeedingSchema.CreateRuns(sqlServer)),
        })
        {
            if (!await TableExistsAsync(connection, sqlServer, table, ct))
                await db.Database.ExecuteSqlRawAsync(sql, ct);
        }

        foreach (var table in new[] { "Users", "DiscussionReplies" })
        {
            if (!await ColumnExistsAsync(connection, sqlServer, table, "SeedRunId", ct))
            {
                var type = sqlServer ? "uniqueidentifier" : "TEXT";
                // Identifiers come only from the constants above, never request data.
                var constraint = sqlServer ? $"CONSTRAINT FK_{table}_DiscussionSeedRuns_SeedRunId " : "";
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {table} ADD SeedRunId {type} NULL {constraint}REFERENCES DiscussionSeedRuns(Id)", ct);
            }
            if (!await ColumnExistsAsync(connection, sqlServer, table, "SeedHiddenAt", ct))
            {
                var type = sqlServer ? "datetime2" : "TEXT";
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {table} ADD SeedHiddenAt {type} NULL", ct);
            }
        }

        if (!await TableExistsAsync(connection, sqlServer, "DiscussionSeedJobs", ct))
            await db.Database.ExecuteSqlRawAsync(DiscussionSeedingSchema.CreateJobs(sqlServer), ct);

        foreach (var index in DiscussionSeedingSchema.Indexes)
        {
            var create = $"CREATE {(index.Unique ? "UNIQUE " : "")}INDEX {index.Name} ON {index.Table} ({index.Columns})";
            var sql = sqlServer
                ? $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{index.Name}' AND object_id = OBJECT_ID('{index.Table}')) {create}"
                : create.Replace("INDEX ", "INDEX IF NOT EXISTS ");
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }
        // Never overwrite switches already set by an operator on a subsequent startup.
        await db.Database.ExecuteSqlRawAsync(DiscussionSeedingSchema.SeedControl, ct);
        await EnsureDiscussionSeedingPipelineSchemaAsync(db, connection, sqlServer, ct);
    }
}
