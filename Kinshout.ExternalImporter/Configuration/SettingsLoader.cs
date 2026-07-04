using System.Text.Json;

namespace Kinshout.ExternalImporter.Configuration;

public static partial class SettingsLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ImporterSettings Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Configuration file not found: {path}");

        var settings = JsonSerializer.Deserialize<ImporterSettings>(File.ReadAllText(path), JsonOptions)
            ?? new ImporterSettings();

        ApplyEnvironmentOverrides(settings);
        ExpandPlaceholders(settings);
        return settings;
    }

    private static void ExpandPlaceholders(ImporterSettings settings)
    {
        foreach (var provider in settings.Providers)
        {
            provider.AccessToken = Expand(provider.AccessToken);
            provider.ApiKey = Expand(provider.ApiKey);
            provider.ApifyToken = Expand(provider.ApifyToken);
            provider.ApifyActorId = Expand(provider.ApifyActorId);
            provider.RecentUrl = Expand(provider.RecentUrl);
            provider.PopularUrl = Expand(provider.PopularUrl);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in provider.Headers)
                headers[name] = Expand(value) ?? value;
            provider.Headers = headers;
        }
    }

    private static string? Expand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains("${", StringComparison.Ordinal))
            return value;

        return PlaceholderRegex().Replace(value, match =>
        {
            var envName = match.Groups[1].Value;
            return Environment.GetEnvironmentVariable(envName) ?? match.Value;
        });
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial System.Text.RegularExpressions.Regex PlaceholderRegex();

    private static void ApplyEnvironmentOverrides(ImporterSettings settings)
    {
        Override(Environment.GetEnvironmentVariable("KINSHOUT_IMPORTER_API_BASE_URL"), value => settings.KinshoutApi.BaseUrl = value);
        Override(Environment.GetEnvironmentVariable("KINSHOUT_IMPORTER_IMPORT_KEY"), value => settings.KinshoutApi.ImportKey = value);
        Override(Environment.GetEnvironmentVariable("KINSHOUT_IMPORTER_IMPORT_PATH"), value => settings.KinshoutApi.ImportPath = value);
        Override(Environment.GetEnvironmentVariable("KINSHOUT_IMPORTER_BATCH_SIZE"), value =>
        {
            if (int.TryParse(value, out var batchSize))
                settings.KinshoutApi.BatchSize = batchSize;
        });
        Override(Environment.GetEnvironmentVariable("KINSHOUT_IMPORTER_MAX_AGE_DAYS"), value =>
        {
            if (int.TryParse(value, out var maxAgeDays))
                settings.Schedule.MaxAdvertAgeDays = maxAgeDays;
        });
        Override(Environment.GetEnvironmentVariable("APIFY_TOKEN"), value =>
        {
            foreach (var provider in settings.Providers)
            {
                if (string.IsNullOrWhiteSpace(provider.ApifyToken))
                    provider.ApifyToken = value;
            }
        });
        Override(Environment.GetEnvironmentVariable("JIJI_COOKIE"), value =>
        {
            foreach (var provider in settings.Providers)
            {
                if (provider.Type.Contains("jiji", StringComparison.OrdinalIgnoreCase)
                    || provider.Provider.Contains("jiji", StringComparison.OrdinalIgnoreCase))
                {
                    provider.Headers["Cookie"] = value;
                }
            }
        });
    }

    private static void Override(string? value, Action<string> apply)
    {
        if (!string.IsNullOrWhiteSpace(value))
            apply(value.Trim().Trim('\r', '\n'));
    }
}
