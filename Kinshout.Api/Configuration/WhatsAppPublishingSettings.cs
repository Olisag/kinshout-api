namespace Kinshout.Api.Configuration;

/// <summary>
/// WhatsApp channel engine. Enabled stays false until production activation is authorized.
/// Dry-run prepares messages and does not call Wapito.
/// </summary>
public sealed class WhatsAppPublishingSettings
{
    public const string SectionName = "WhatsAppPublishing";

    public bool Enabled { get; set; }

    public bool DryRun { get; set; } = true;

    public string TimeZone { get; set; } = "Africa/Kinshasa";

    /// <summary>The existing Kinoiserie channel. Not a phone number and not a group.</summary>
    public string ChannelId { get; set; } = "120363412372960130@newsletter";

    public string TestChannelId { get; set; } = "";

    public bool UseTestDestination { get; set; }

    public int MaxDailyPublications { get; set; } = 15;

    public int MinimumPublicationSpacingMinutes { get; set; } = 20;

    public int EditorialDuplicateWindowDays { get; set; } = 7;

    public int MaximumRetryAttempts { get; set; } = 2;

    public int TickSeconds { get; set; } = 60;

    public int MinTrafficSegments { get; set; } = 3;

    public int MaxTrafficSegments { get; set; } = 10;

    public int TrafficMaxObservationAgeMinutes { get; set; } = 30;

    public int MinHeavySegmentsForMidday { get; set; } = 2;

    public bool WeatherEnabled { get; set; } = true;

    public bool TrafficEnabled { get; set; } = true;

    public bool EditorialEnabled { get; set; } = true;

    public bool InteractionEnabled { get; set; } = true;

    public bool EventsEnabled { get; set; } = true;

    /// <summary>Noon forecast check. Off until a second daily bulletin is wanted.</summary>
    public bool WeatherForecastRefreshEnabled { get; set; }

    /// <summary>Midday Google Maps collection. Off so the extra Apify run is not spent by default.</summary>
    public bool MiddayTrafficEnabled { get; set; }

    /// <summary>
    /// Open-Meteo forecast data is CC BY 4.0. Turn this on before a public bulletin if that licence applies to the use.
    /// The proof of concept omitted the line on request, so the default stays off.
    /// </summary>
    public bool WeatherAttributionRequired { get; set; }

    /// <summary>
    /// Google Maps driving times were not licensed for republication during the proof of concept.
    /// Live traffic posts stay blocked until this is explicitly set.
    /// </summary>
    public bool TrafficRedistributionPermitted { get; set; }

    public double Latitude { get; set; } = -4.3217;

    public double Longitude { get; set; } = 15.3119;

    public int RainProbabilityHighlightMinimum { get; set; } = 20;

    public decimal CostPerTrafficRouteUsd { get; set; } = 0.002m;

    public int MaxApifyRunsPerDay { get; set; } = 4;

    public decimal MaxDailyApifySpend { get; set; } = 0.20m;

    public decimal MaxMonthlyApifySpend { get; set; } = 6.00m;

    /// <summary>Sandbox plan, from December 2026, allows 500 API requests a month. Leave headroom for status checks.</summary>
    public int MaxMonthlyWapitoRequests { get; set; } = 400;

    public decimal MaxMonthlyAiSpend { get; set; }

    public string WebBaseUrl { get; set; } = "https://kinoiserie.com";

    public string WapitoBaseUrl { get; set; } = "https://api.wapito.com/v1";

    public string WapitoToken { get; set; } = "";

    public string OpenMeteoBaseUrl { get; set; } = "https://api.open-meteo.com/v1/forecast";

    public string ApifyToken { get; set; } = "";

    public string ApifyActorId { get; set; } = "rastriq~google-maps-directions-traffic";

    /// <summary>Separate from the feed-import token so discovery spend is not used here.</summary>
    public string AdminKey { get; set; } = "";
}
