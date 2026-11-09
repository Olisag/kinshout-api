using Kinshout.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations;

[DbContext(typeof(KinshoutDbContext))]
[Migration("20261108230000_AddDiscussionSeedingRollback")]
public sealed class AddDiscussionSeedingRollback : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var sqlServer = ActiveProvider.Contains("SqlServer");
        migrationBuilder.Sql(DiscussionSeedingSchema.CreateControl(sqlServer));
        migrationBuilder.Sql(DiscussionSeedingSchema.CreateRuns(sqlServer));
        foreach (var table in new[] { "Users", "DiscussionReplies" })
        {
            var idType = sqlServer ? "uniqueidentifier" : "TEXT";
            var dateType = sqlServer ? "datetime2" : "TEXT";
            var constraint = sqlServer ? $"CONSTRAINT FK_{table}_DiscussionSeedRuns_SeedRunId " : "";
            migrationBuilder.Sql($"ALTER TABLE {table} ADD SeedRunId {idType} NULL {constraint}REFERENCES DiscussionSeedRuns(Id)");
            migrationBuilder.Sql($"ALTER TABLE {table} ADD SeedHiddenAt {dateType} NULL");
        }
        migrationBuilder.Sql(DiscussionSeedingSchema.CreateJobs(sqlServer));
        foreach (var index in DiscussionSeedingSchema.Indexes)
        {
            migrationBuilder.CreateIndex(index.Name, index.Table,
                index.Columns.Split(", "), unique: index.Unique);
        }
        migrationBuilder.Sql(DiscussionSeedingSchema.SeedControl);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Emergency rollback uses stop-and-hide and an application rollback. Downgrade
        // is only for a disposable/prelaunch database; it removes provenance and job history.
        migrationBuilder.DropTable("DiscussionSeedJobs");
        migrationBuilder.DropTable("DiscussionSeedingControls");
        foreach (var table in new[] { "Users", "DiscussionReplies" })
        {
            if (ActiveProvider.Contains("SqlServer"))
                migrationBuilder.Sql($"ALTER TABLE {table} DROP CONSTRAINT FK_{table}_DiscussionSeedRuns_SeedRunId");
            migrationBuilder.DropIndex($"IX_{table}_SeedRunId", table);
            migrationBuilder.Sql($"ALTER TABLE {table} DROP COLUMN SeedRunId");
            migrationBuilder.Sql($"ALTER TABLE {table} DROP COLUMN SeedHiddenAt");
        }
        migrationBuilder.DropTable("DiscussionSeedRuns");
    }
}
