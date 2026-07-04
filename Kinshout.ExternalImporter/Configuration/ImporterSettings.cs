namespace Kinshout.ExternalImporter.Configuration;

public sealed class ImporterSettings
{
    public KinshoutApiSettings KinshoutApi { get; set; } = new();
    public ImportScheduleSettings Schedule { get; set; } = new();
    public List<ExternalProviderSettings> Providers { get; set; } = [];
}

public sealed class KinshoutApiSettings
{
    public string BaseUrl { get; set; } = "http://localhost:5000";
    public string ImportPath { get; set; } = "/api/imports/adverts";
    public string KnownAdvertsPath { get; set; } = "/api/imports/known-adverts";
    public string ImportKey { get; set; } = "";
    public int BatchSize { get; set; } = 100;
}

public sealed class ImportScheduleSettings
{
    public bool RunOnce { get; set; } = true;
    public int IntervalHours { get; set; } = 24;
    public int MaxAdvertAgeDays { get; set; } = 60;
    /// <summary>When set, the daemon waits until this local hour (see TimeZoneId) before each run.</summary>
    public int? RunAtHour { get; set; } = 3;
    public string TimeZoneId { get; set; } = "Africa/Kinshasa";
    /// <summary>Skip adverts already stored in Kinshout (by provider + external id).</summary>
    public bool SkipExisting { get; set; } = true;
    /// <summary>Delete Kinshout rows for provider listings missing from the latest source crawl.</summary>
    public bool DetectRemovedListings { get; set; } = true;
}

public sealed class ExternalProviderSettings
{
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? ProviderName { get; set; }
    public string Type { get; set; } = "json-feed";
    public bool Enabled { get; set; } = true;
    public string? RecentUrl { get; set; }
    public string? PopularUrl { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string DefaultCategory { get; set; } = "immobilier";
    public string? DefaultSubcategory { get; set; }
    public string DefaultCity { get; set; } = "Kinshasa";
    public string? DefaultCommune { get; set; }
    public string DefaultModality { get; set; } = "rent";
    public string DefaultLanguage { get; set; } = "fr";
    public int MaxPages { get; set; } = 5;
    public bool FetchDetails { get; set; } = true;
    public int RequestDelayMs { get; set; } = 400;
    public string? AccessToken { get; set; }
    public string? ApiKey { get; set; }
    public string? ApifyToken { get; set; }
    public string? ApifyActorId { get; set; }
    public string? MarketplaceLocation { get; set; }
    public int ResultsLimit { get; set; }
    public int ActorTimeoutSeconds { get; set; } = 300;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int SearchRadiusKm { get; set; } = 65;
    public List<string> SearchQueries { get; set; } = [];
    public List<string> ExtraListingUrls { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlySet<string>? KnownAdvertKeys { get; set; }
}
