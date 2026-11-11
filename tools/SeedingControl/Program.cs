using System.Text.Json;
using Kinshout.Api.Data;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;

var action = args.SingleOrDefault();
if (action is not ("preview" or "stop-and-hide" or "pause" or "resume" or "stop" or "report"))
{
    Console.Error.WriteLine("Usage: SeedingControl preview|stop-and-hide|pause|resume|stop|report");
    return 2;
}
var connection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine("Configure ConnectionStrings__DefaultConnection securely.");
    return 2;
}
var options = new DbContextOptionsBuilder<KinshoutDbContext>();
if (connection.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase) && !connection.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase)) options.UseSqlite(connection);
else options.UseSqlServer(connection, sql => sql.EnableRetryOnFailure());
var service = new DiscussionSeedingService(options.Options);
try
{
    object result;
    switch (action)
    {
        case "preview": result = await service.PreviewRollbackAsync(); break;
        case "stop-and-hide": result = await service.StopAndHideAsync(Guid.Empty); break;
        case "pause":
            var flags = await service.GetFlagsAsync();
            result = await service.SetFlagsAsync(Guid.Empty, flags with { GenerationEnabled = false, PublishingEnabled = false }); break;
        case "stop": result = new { stoppedRuns = await service.StopAsync(Guid.Empty) }; break;
        case "resume":
            await using (var db = new KinshoutDbContext(options.Options))
                if (!await db.DiscussionSeedRuns.AnyAsync(r => r.StoppedAt == null))
                    await service.CreateRunAsync(Guid.Empty, "Explicit operator resume");
            result = await service.SetFlagsAsync(Guid.Empty, new(true, true, true)); break;
        default:
            await using (var db = new KinshoutDbContext(options.Options))
            {
                result = new
                {
                    label = "Simulation is excluded from genuine traction",
                    utc = DateTime.UtcNow,
                    flags = await service.GetFlagsAsync(),
                    activeRuns = await db.DiscussionSeedRuns.CountAsync(r => r.StoppedAt == null),
                    simulatedPublicCommunities = await db.Communities.CountAsync(c => c.IsActive && c.Visibility == "public" && c.CreatedByUser.SeedRunId != null),
                    simulatedDiscussionPosts = await db.Discussions.CountAsync(d => d.User.SeedRunId != null),
                    simulatedProfiles = await db.Users.CountAsync(u => u.SeedRunId != null && u.SeedHiddenAt == null),
                    genuineUsers = await db.Users.CountAsync(u => u.SeedRunId == null && !u.Email.EndsWith("@kinshout.system")),
                    genuineComments = await db.DiscussionReplies.CountAsync(r => r.SeedRunId == null && r.User.SeedRunId == null && !r.User.Email.EndsWith("@kinshout.system")),
                    simulatedComments = await db.DiscussionReplies.CountAsync(r => r.SeedRunId != null),
                    genuineLikes = await db.LikedDiscussions.CountAsync(l => l.User.SeedRunId == null && !l.User.Email.EndsWith("@kinshout.system")),
                    simulatedLikes = await db.DiscussionSimulatedLikes.CountAsync(l => l.HiddenAt == null),
                    simulatedCommentsToday = await db.DiscussionReplies.CountAsync(r => r.SeedRunId != null && r.CreatedAt >= DateTime.UtcNow.Date),
                    simulatedLikesToday = await db.DiscussionSimulatedLikes.CountAsync(l => l.HiddenAt == null && l.CreatedAt >= DateTime.UtcNow.Date),
                    lastPublishedComment = await db.DiscussionSeedJobs.MaxAsync(j => j.PublishedAt),
                    lastPublishedLike = await db.DiscussionSimulatedLikes.MaxAsync(l => (DateTime?)l.CreatedAt),
                    budgetReservationsNotInvoice = await db.DiscussionSimulationDays.AsNoTracking().OrderByDescending(d => d.Day).Take(7).ToListAsync(),
                };
            }
            break;
    }
    Console.WriteLine(JsonSerializer.Serialize(result));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Operation failed: {ex.GetType().Name}");
    return 1;
}
