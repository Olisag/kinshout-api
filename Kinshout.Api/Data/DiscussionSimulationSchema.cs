using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Kinshout.Api.Data;

internal static class DiscussionSimulationSchema
{
    internal static IEnumerable<(string Table, string Ddl)> Tables(bool server)
    {
        var guid = server ? "uniqueidentifier" : "TEXT";
        var date = server ? "datetime2" : "TEXT";
        yield return ("DiscussionSimulatedLikes", $"""
            CREATE TABLE DiscussionSimulatedLikes (
                Id {guid} NOT NULL PRIMARY KEY,
                SeedRunId {guid} NOT NULL REFERENCES DiscussionSeedRuns(Id),
                DiscussionId {guid} NOT NULL REFERENCES Discussions(Id) ON DELETE CASCADE,
                PersonaUserId {guid} NOT NULL REFERENCES Users(Id),
                CreatedAt {date} NOT NULL, HiddenAt {date} NULL,
                CONSTRAINT UQ_SimulatedLike_Post_Persona UNIQUE (DiscussionId, PersonaUserId)
            );
            CREATE INDEX IX_DiscussionSimulatedLikes_SeedRunId ON DiscussionSimulatedLikes(SeedRunId);
            CREATE INDEX IX_DiscussionSimulatedLikes_CreatedAt ON DiscussionSimulatedLikes(CreatedAt);
            """);
        yield return ("DiscussionSimulationDays", $"""
            CREATE TABLE DiscussionSimulationDays (
                Day {date} NOT NULL PRIMARY KEY,
                GenerationAttempts int NOT NULL,
                ReservedCostUsd decimal(12,6) NOT NULL,
                LastAttemptAt {date} NULL, LastLikeAt {date} NULL, HeartbeatAt {date} NULL
            );
            """);
    }
}

[DbContext(typeof(KinshoutDbContext))]
[Migration("20261111010000_AddEngagementSimulation")]
public sealed class AddEngagementSimulation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var (_, ddl) in DiscussionSimulationSchema.Tables(ActiveProvider?.Contains("SqlServer") == true))
            migrationBuilder.Sql(ddl);
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Simulation provenance must be retained; use explicit manual rollback.");
}
