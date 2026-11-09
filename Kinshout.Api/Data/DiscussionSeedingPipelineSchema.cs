using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Kinshout.Api.Data;

internal static class DiscussionSeedingPipelineSchema
{
    internal static readonly (string Table, string Column, string ServerType, string SqliteType, string Default)[] Columns =
    [
        ("Discussions", "SourceLikeCount", "bigint", "INTEGER", "NULL"),
        ("Discussions", "SourceViewCount", "bigint", "INTEGER", "NULL"),
        ("Discussions", "SourceMetricsObservedAt", "datetime2", "TEXT", "NULL"),
        ("Discussions", "SourceReactionTextsJson", "nvarchar(max)", "TEXT", "NOT NULL DEFAULT '[]'"),
        ("Discussions", "SourceMetricsRevision", "nvarchar(64)", "TEXT", "NULL"),
        ("Discussions", "SeedProcessedRevision", "nvarchar(64)", "TEXT", "NULL"),
        ("DiscussionSeedJobs", "CreatedAt", "datetime2", "TEXT", "NOT NULL DEFAULT '1970-01-01'"),
        ("DiscussionSeedJobs", "SourceRevision", "nvarchar(64)", "TEXT", "NULL"),
    ];
}

public static partial class DbSchemaPatcher
{
    private static async Task EnsureDiscussionSeedingPipelineSchemaAsync(KinshoutDbContext db,
        System.Data.Common.DbConnection connection, bool sqlServer, CancellationToken ct)
    {
        foreach (var c in DiscussionSeedingPipelineSchema.Columns)
        {
            if (await ColumnExistsAsync(connection, sqlServer, c.Table, c.Column, ct)) continue;
            var sql = $"ALTER TABLE {c.Table} ADD {c.Column} {(sqlServer ? c.ServerType : c.SqliteType)} {c.Default}";
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }
    }
}

[DbContext(typeof(KinshoutDbContext))]
[Migration("20261108235000_AddDiscussionSeedingPipeline")]
public sealed class AddDiscussionSeedingPipeline : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var server = ActiveProvider?.Contains("SqlServer") == true;
        foreach (var c in DiscussionSeedingPipelineSchema.Columns)
            migrationBuilder.Sql($"ALTER TABLE {c.Table} ADD {c.Column} {(server ? c.ServerType : c.SqliteType)} {c.Default}");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Use stop-and-hide and retain provenance/source metrics during application rollback.");
}
