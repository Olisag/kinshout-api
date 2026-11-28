using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Data;

public static partial class DbSchemaPatcher
{
    private static async Task EnsureWhatsAppChannelSchemaAsync(
        KinshoutDbContext db, System.Data.Common.DbConnection connection, bool sqlServer, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, sqlServer, "WhatsAppPublications", ct))
        {
            var id = sqlServer ? "uniqueidentifier" : "TEXT";
            var text = sqlServer ? "nvarchar(200)" : "TEXT";
            var body = sqlServer ? "nvarchar(max)" : "TEXT";
            var date = sqlServer ? "datetime2" : "TEXT";
            var flag = sqlServer ? "bit" : "INTEGER";
            var money = sqlServer ? "decimal(12,6)" : "TEXT";
            var discussion = sqlServer ? "uniqueidentifier" : "TEXT";
            await db.Database.ExecuteSqlRawAsync($"""
                CREATE TABLE WhatsAppPublications (
                    Id {id} NOT NULL PRIMARY KEY,
                    IdempotencyKey {text} NOT NULL,
                    ChannelId nvarchar(80) NOT NULL,
                    Category nvarchar(40) NOT NULL,
                    Purpose nvarchar(20) NOT NULL,
                    Slot nvarchar(80) NOT NULL,
                    LocalDate nvarchar(10) NOT NULL,
                    ScheduledAtUtc {date} NOT NULL,
                    CreatedAtUtc {date} NOT NULL,
                    Body {body} NULL,
                    DiscussionId {discussion} NULL,
                    CanonicalUrl nvarchar(500) NULL,
                    ProviderName nvarchar(40) NULL,
                    ProviderReference nvarchar(120) NULL,
                    ObservedAtUtc {date} NULL,
                    Status nvarchar(32) NOT NULL,
                    ProviderMessageId nvarchar(180) NULL,
                    SubmittedAtUtc {date} NULL,
                    ConfirmedAtUtc {date} NULL,
                    RetryCount int NOT NULL,
                    LastError nvarchar(200) NULL,
                    Retryable {flag} NOT NULL,
                    EstimatedCostUsd {money} NULL,
                    Manual {flag} NOT NULL,
                    TraceJson {body} NULL
                );
                """, ct);
            await db.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IX_WhatsAppPublications_IdempotencyKey ON WhatsAppPublications (IdempotencyKey);", ct);
        }

        if (!await TableExistsAsync(connection, sqlServer, "WhatsAppSpends", ct))
        {
            var id = sqlServer ? "uniqueidentifier" : "TEXT";
            var date = sqlServer ? "datetime2" : "TEXT";
            var money = sqlServer ? "decimal(12,6)" : "TEXT";
            await db.Database.ExecuteSqlRawAsync($"""
                CREATE TABLE WhatsAppSpends (
                    Id {id} NOT NULL PRIMARY KEY,
                    Provider nvarchar(20) NOT NULL,
                    SlotKey nvarchar(220) NOT NULL,
                    OccurredAtUtc {date} NOT NULL,
                    EstimatedCostUsd {money} NOT NULL,
                    Kind nvarchar(20) NOT NULL
                );
                """, ct);
            await db.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IX_WhatsAppSpends_SlotKey ON WhatsAppSpends (SlotKey);", ct);
        }

        if (!await TableExistsAsync(connection, sqlServer, "WhatsAppChannelControls", ct))
        {
            var flag = sqlServer ? "bit" : "INTEGER";
            await db.Database.ExecuteSqlRawAsync($"""
                CREATE TABLE WhatsAppChannelControls (
                    Id int NOT NULL PRIMARY KEY,
                    Paused {flag} NOT NULL
                );
                """, ct);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO WhatsAppChannelControls (Id, Paused) VALUES (1, 0);", ct);
        }
    }
}
