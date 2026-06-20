namespace Kinshout.Api.Configuration;

public class UploadStorageSettings
{
    public const string SectionName = "UploadStorage";
    public string AzureBlobConnectionString { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "uploads";
    public bool UseAzureBlob => !string.IsNullOrWhiteSpace(AzureBlobConnectionString);
}
