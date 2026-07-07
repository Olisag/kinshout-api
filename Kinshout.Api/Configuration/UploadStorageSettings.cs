namespace Kinshout.Api.Configuration;

public class UploadStorageSettings
{
    public const string SectionName = "UploadStorage";
    public string AzureBlobConnectionString { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "uploads";
    /// <summary>Public API base URL for absolute upload links (e.g. https://kinshout-api-dev.azurewebsites.net).</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;
    public bool UseAzureBlob => !string.IsNullOrWhiteSpace(AzureBlobConnectionString);
}
