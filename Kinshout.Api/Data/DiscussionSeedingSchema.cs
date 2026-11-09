namespace Kinshout.Api.Data;

/// <summary>Shared DDL for the migration and the legacy schema-repair path.</summary>
internal static class DiscussionSeedingSchema
{
    internal static string CreateControl(bool sqlServer) => sqlServer
        ? """
          CREATE TABLE DiscussionSeedingControls (
              Id int NOT NULL PRIMARY KEY,
              GenerationEnabled bit NOT NULL,
              PublishingEnabled bit NOT NULL,
              CombinedLikesEnabled bit NOT NULL,
              UpdatedAt datetime2 NULL,
              UpdatedByUserId uniqueidentifier NULL
          );
          """
        : """
          CREATE TABLE DiscussionSeedingControls (
              Id INTEGER NOT NULL PRIMARY KEY,
              GenerationEnabled INTEGER NOT NULL,
              PublishingEnabled INTEGER NOT NULL,
              CombinedLikesEnabled INTEGER NOT NULL,
              UpdatedAt TEXT NULL,
              UpdatedByUserId TEXT NULL
          );
          """;

    internal static string CreateRuns(bool sqlServer) => sqlServer
        ? """
          CREATE TABLE DiscussionSeedRuns (
              Id uniqueidentifier NOT NULL PRIMARY KEY,
              Name nvarchar(120) NOT NULL,
              CreatedAt datetime2 NOT NULL,
              CreatedByUserId uniqueidentifier NOT NULL,
              StoppedAt datetime2 NULL,
              StoppedByUserId uniqueidentifier NULL
          );
          """
        : """
          CREATE TABLE DiscussionSeedRuns (
              Id TEXT NOT NULL PRIMARY KEY,
              Name TEXT NOT NULL,
              CreatedAt TEXT NOT NULL,
              CreatedByUserId TEXT NOT NULL,
              StoppedAt TEXT NULL,
              StoppedByUserId TEXT NULL
          );
          """;

    internal static string CreateJobs(bool sqlServer) => sqlServer
        ? """
          CREATE TABLE DiscussionSeedJobs (
              Id uniqueidentifier NOT NULL PRIMARY KEY,
              SeedRunId uniqueidentifier NOT NULL REFERENCES DiscussionSeedRuns(Id),
              DiscussionId uniqueidentifier NOT NULL REFERENCES Discussions(Id) ON DELETE CASCADE,
              PersonaUserId uniqueidentifier NOT NULL REFERENCES Users(Id),
              IdempotencyKey nvarchar(128) NOT NULL,
              Body nvarchar(4000) NOT NULL,
              Status nvarchar(16) NOT NULL,
              ScheduledAt datetime2 NOT NULL,
              PublishedAt datetime2 NULL,
              CancelledAt datetime2 NULL
          );
          """
        : """
          CREATE TABLE DiscussionSeedJobs (
              Id TEXT NOT NULL PRIMARY KEY,
              SeedRunId TEXT NOT NULL REFERENCES DiscussionSeedRuns(Id),
              DiscussionId TEXT NOT NULL REFERENCES Discussions(Id) ON DELETE CASCADE,
              PersonaUserId TEXT NOT NULL REFERENCES Users(Id),
              IdempotencyKey TEXT NOT NULL,
              Body TEXT NOT NULL,
              Status TEXT NOT NULL,
              ScheduledAt TEXT NOT NULL,
              PublishedAt TEXT NULL,
              CancelledAt TEXT NULL
          );
          """;

    internal const string SeedControl = """
        INSERT INTO DiscussionSeedingControls (Id, GenerationEnabled, PublishingEnabled, CombinedLikesEnabled)
        SELECT 1, 0, 0, 0 WHERE NOT EXISTS (SELECT 1 FROM DiscussionSeedingControls WHERE Id = 1);
        """;

    internal static readonly (string Name, string Table, string Columns, bool Unique)[] Indexes =
    [
        ("IX_DiscussionSeedRuns_StoppedAt", "DiscussionSeedRuns", "StoppedAt", false),
        ("IX_DiscussionSeedJobs_IdempotencyKey", "DiscussionSeedJobs", "IdempotencyKey", true),
        ("IX_DiscussionSeedJobs_SeedRunId_Status", "DiscussionSeedJobs", "SeedRunId, Status", false),
        ("IX_DiscussionSeedJobs_Status_ScheduledAt", "DiscussionSeedJobs", "Status, ScheduledAt", false),
        ("IX_DiscussionSeedJobs_DiscussionId", "DiscussionSeedJobs", "DiscussionId", false),
        ("IX_DiscussionSeedJobs_PersonaUserId", "DiscussionSeedJobs", "PersonaUserId", false),
        ("IX_Users_SeedRunId", "Users", "SeedRunId", false),
        ("IX_DiscussionReplies_SeedRunId", "DiscussionReplies", "SeedRunId", false),
    ];
}
