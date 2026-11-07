using Kinshout.ExternalImporter.Configuration;
using Kinshout.ExternalImporter.Import;

DotEnvLoader.Load();

var configPath = GetOption(args, "--config") ?? "appsettings.json";
var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
var once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
var discussionsOnly = args.Contains("--discussions", StringComparer.OrdinalIgnoreCase);
var advertsOnly = args.Contains("--adverts", StringComparer.OrdinalIgnoreCase);
var webSub = args.Contains("--youtube-websub", StringComparer.OrdinalIgnoreCase);
var youtubeStats = args.Contains("--youtube-stats", StringComparer.OrdinalIgnoreCase);
var classiquesBackfill = args.Contains("--classiques-backfill", StringComparer.OrdinalIgnoreCase);
var youtubeBuzz = args.Contains("--youtube-buzz", StringComparer.OrdinalIgnoreCase);
var dropUnavailable = args.Contains("--drop-unavailable", StringComparer.OrdinalIgnoreCase);
var providerFilter = GetOption(args, "--providers");

var settings = SettingsLoader.Load(configPath);
if (once || webSub || youtubeStats || classiquesBackfill || youtubeBuzz || dropUnavailable)
    settings.Schedule.RunOnce = true;
settings.Schedule.ClassiquesBackfill = classiquesBackfill;
settings.Schedule.YouTubeBuzz = youtubeBuzz;
if (!string.IsNullOrWhiteSpace(providerFilter))
{
    foreach (var provider in providerFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        settings.Schedule.ProviderFilter.Add(provider);
}

using var http = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(320),
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

async Task RunScheduledAsync()
{
    if (settings.Schedule.RunOnce)
    {
        await RunImportsAsync();
        return;
    }

    Console.WriteLine($"Import scheduler active — next run at {ImportScheduleHelper.DescribeNextRun(settings.Schedule, DateTime.UtcNow)}.");

    while (!cts.IsCancellationRequested)
    {
        var delay = ImportScheduleHelper.DelayUntilNextRun(settings.Schedule, DateTime.UtcNow);
        if (delay > TimeSpan.Zero)
        {
            Console.WriteLine($"Waiting {delay:g} until next scheduled import…");
            await Task.Delay(delay, cts.Token);
        }

        await RunImportsAsync();
        Console.WriteLine($"Next run at {ImportScheduleHelper.DescribeNextRun(settings.Schedule, DateTime.UtcNow)}.");
    }
}

async Task RunImportsAsync()
{
    if (dropUnavailable)
    {
        await new KinshoutImportClient(http, settings.KinshoutApi).DropUnavailableSourcesAsync(cts.Token);
        return;
    }

    if (webSub)
    {
        await new YouTubeRegistryJobs(http, settings).RenewWebSubAsync(cts.Token);
        return;
    }

    if (youtubeStats)
    {
        await new YouTubeRegistryJobs(http, settings).RefreshStatsAsync(cts.Token);
        return;
    }

    if (!discussionsOnly && !classiquesBackfill && !youtubeBuzz)
    {
        var advertRunner = new ExternalImportRunner(http, settings, dryRun);
        await advertRunner.RunOnceAsync(cts.Token);
    }

    if (!advertsOnly)
    {
        var discussionRunner = new ExternalDiscussionImportRunner(http, settings, dryRun);
        await discussionRunner.RunOnceAsync(cts.Token);
    }
}

await RunScheduledAsync();

static string? GetOption(string[] args, string name)
{
    var index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
