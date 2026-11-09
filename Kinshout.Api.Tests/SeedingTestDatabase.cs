using Kinshout.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Tests;

/// <summary>Each case owns a new database; never cleans or reuses an application's database.</summary>
internal sealed class SeedingTestDatabase(DbContextOptions<KinshoutDbContext> options) : IAsyncDisposable
{
    public DbContextOptions<KinshoutDbContext> Options { get; } = options;

    public static async Task<SeedingTestDatabase> CreateAsync()
    {
        var connection = Environment.GetEnvironmentVariable("KINSHOUT_SQLSERVER_TEST_CONNECTION");
        var builder = new DbContextOptionsBuilder<KinshoutDbContext>();
        if (string.IsNullOrWhiteSpace(connection))
        {
            var file = Path.Combine(Path.GetTempPath(), "kinoiserie-seeding-" + Guid.NewGuid().ToString("N") + ".db");
            builder.UseSqlite($"Data Source={file};Pooling=False;Default Timeout=10");
        }
        else
        {
            var sql = new SqlConnectionStringBuilder(connection);
            var host = sql.DataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)
                ? sql.DataSource[4..] : sql.DataSource;
            host = host.Split(',')[0];
            var local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
            if (!local && Environment.GetEnvironmentVariable("KINSHOUT_SQLSERVER_ALLOW_REMOTE_TEST_SERVER") != "true")
                throw new InvalidOperationException("Remote SQL validation requires an explicitly approved isolated test server.");
            sql.InitialCatalog = "KinshoutSeedingValidation_" + Guid.NewGuid().ToString("N");
            sql.Pooling = false;
            sql.ConnectTimeout = 10;
            builder.UseSqlServer(sql.ConnectionString, server => server.EnableRetryOnFailure(3, TimeSpan.FromSeconds(1), null));
        }
        var database = new SeedingTestDatabase(builder.Options);
        await using var db = new KinshoutDbContext(database.Options);
        try { await db.Database.EnsureCreatedAsync(); }
        catch
        {
            try { await db.Database.EnsureDeletedAsync(); } catch { /* Preserve the original creation failure. */ }
            throw;
        }
        return database;
    }

    public static async Task DropColumnAsync(KinshoutDbContext db, string table, string column)
    {
        if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DECLARE @constraint sysname;
                SELECT @constraint = dc.name
                FROM sys.default_constraints dc
                JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
                WHERE dc.parent_object_id = OBJECT_ID({table}) AND c.name = {column};
                IF @constraint IS NOT NULL
                BEGIN
                    DECLARE @ddl nvarchar(max) = N'ALTER TABLE ' + QUOTENAME({table}) + N' DROP CONSTRAINT ' + QUOTENAME(@constraint);
                    EXEC sp_executesql @ddl;
                END
                """);
        }
        // The identifiers are constants from DiscussionSeedingPipelineSchema, not request input.
        var sql = $"ALTER TABLE {table} DROP COLUMN {column}";
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = new KinshoutDbContext(Options);
        await db.Database.EnsureDeletedAsync();
    }
}
