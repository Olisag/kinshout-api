using Kinshout.ExternalImporter.Configuration;
using Kinshout.ExternalImporter.Import;

DotEnvLoader.Load();

var configPath = GetOption(args, "--config") ?? "appsettings.json";
var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
var once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);

var settings = SettingsLoader.Load(configPath);
if (once)
    settings.Schedule.RunOnce = true;

using var http = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(320),
};

var runner = new ExternalImportRunner(http, settings, dryRun);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

if (settings.Schedule.RunOnce)
{
    await runner.RunOnceAsync(cts.Token);
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

    await runner.RunOnceAsync(cts.Token);
    Console.WriteLine($"Next run at {ImportScheduleHelper.DescribeNextRun(settings.Schedule, DateTime.UtcNow)}.");
}

static string? GetOption(string[] args, string name)
{
    var index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
