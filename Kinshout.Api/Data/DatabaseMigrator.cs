using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Kinshout.Api.Data;

/// <summary>
/// Applies pending EF migrations. <see cref="DbSchemaPatcher"/> creates the same tables, columns and indexes
/// idempotently, so on databases it has already patched a migration can fail only because its objects already
/// exist. Such a migration is recorded as applied instead of blocking every later one.
/// </summary>
public static class DatabaseMigrator
{
    private const string LockResource = "Kinshout.DatabaseMigrator";

    private static readonly HashSet<int> AlreadyAppliedErrorNumbers =
    [
        1779, // table already has a primary key
        1781, // column already has a default
        1913, // index or statistics already exists
        2705, // column name specified more than once
        2714, // object already exists
        3701, // cannot drop: object does not exist
        4924, // cannot drop column: column does not exist
    ];

    public static async Task MigrateAsync(KinshoutDbContext db, ILogger logger, CancellationToken ct = default)
    {
        // Instances starting together (e.g. during a deploy) must not interleave: the lock is held on this
        // session for the whole loop, and the migrator reuses the same open connection.
        await db.Database.OpenConnectionAsync(ct);
        var locked = false;
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                DECLARE @result int;
                EXEC @result = sp_getapplock @Resource = {0}, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 600000;
                IF @result < 0 THROW 50000, 'Could not acquire the database migration lock.', 1;
                """,
                [LockResource],
                ct);
            locked = true;

            string? lastRecorded = null;
            while (true)
            {
                try
                {
                    // No target migration: EF only applies forward and never reverts.
                    await db.Database.MigrateAsync(ct);
                    return;
                }
                catch (Exception ex) when (FindSqlException(ex) is { } sql && IsAlreadyAppliedError(sql.Number))
                {
                    // Migrations apply in order, each in its own transaction, so the failed one is the first pending.
                    var failed = (await db.Database.GetPendingMigrationsAsync(ct)).FirstOrDefault();
                    if (failed is null || failed == lastRecorded)
                        throw;

                    logger.LogWarning(
                        "Migration {MigrationId} failed because the schema already contains its changes ({Error}). Recording it as applied.",
                        failed,
                        sql.Message);
                    await RecordAsAppliedAsync(db, failed, ct);
                    lastRecorded = failed;
                }
            }
        }
        finally
        {
            if (locked)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "EXEC sp_releaseapplock @Resource = {0}, @LockOwner = 'Session'",
                    [LockResource],
                    CancellationToken.None);
            }

            await db.Database.CloseConnectionAsync();
        }
    }

    internal static bool IsAlreadyAppliedError(int sqlErrorNumber) =>
        AlreadyAppliedErrorNumbers.Contains(sqlErrorNumber);

    private static SqlException? FindSqlException(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is SqlException sql)
                return sql;
        }

        return null;
    }

    private static async Task RecordAsAppliedAsync(KinshoutDbContext db, string migrationId, CancellationToken ct)
    {
        var history = db.GetService<IHistoryRepository>();
        var insert = history.GetInsertScript(new HistoryRow(migrationId, ProductInfo.GetVersion()));
        var sql = "IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = {0}) BEGIN " + insert + " END";
        await db.Database.ExecuteSqlRawAsync(sql, [migrationId], ct);
    }
}
