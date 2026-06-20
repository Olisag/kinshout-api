using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Kinshout.Api.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<KinshoutDbContext>
{
    public KinshoutDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("KINSHOUT_CONNECTION")
            ?? "Data Source=kinshout-dev.db";

        var optionsBuilder = new DbContextOptionsBuilder<KinshoutDbContext>();

        if (connectionString.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
            optionsBuilder.UseSqlite(connectionString);
        else
            optionsBuilder.UseSqlServer(connectionString);

        return new KinshoutDbContext(optionsBuilder.Options);
    }
}
