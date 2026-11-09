using System.Text.Json;
using Kinshout.Api.Data;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;

var action = args.SingleOrDefault();
if (action is not ("preview" or "stop-and-hide"))
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/SeedingControl -- preview|stop-and-hide");
    return 2;
}
var connection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine("Configure ConnectionStrings__DefaultConnection through secure environment configuration.");
    return 2;
}
if (action == "stop-and-hide" && Environment.GetEnvironmentVariable("DiscussionSeeding__Enabled") != "false")
{
    Console.Error.WriteLine("Disable DiscussionSeeding__Enabled=false on every API instance first, then set it here to confirm the worker is stopped.");
    return 2;
}
var options = new DbContextOptionsBuilder<KinshoutDbContext>();
if (connection.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)) options.UseSqlite(connection);
else options.UseSqlServer(connection, sql => sql.EnableRetryOnFailure());
var service = new DiscussionSeedingService(options.Options);
try
{
    var result = action == "preview" ? await service.PreviewRollbackAsync()
        : await service.StopAndHideAsync(Guid.Empty);
    Console.WriteLine(JsonSerializer.Serialize(result));
    return 0;
}
catch (Exception ex)
{
    // Connection strings and SQL exception details must not enter operator reports.
    Console.Error.WriteLine($"Operation failed: {ex.GetType().Name}");
    return 1;
}
