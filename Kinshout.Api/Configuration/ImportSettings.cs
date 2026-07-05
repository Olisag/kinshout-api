namespace Kinshout.Api.Configuration;

public class ImportSettings
{
    public const string SectionName = "Import";
    public string SecretKey { get; set; } = string.Empty;
    /// <summary>Download external listing photos into Kinshout blob/local storage on import.</summary>
    public bool MirrorExternalImages { get; set; } = true;
    public int MirrorImageTimeoutSeconds { get; set; } = 30;
}
