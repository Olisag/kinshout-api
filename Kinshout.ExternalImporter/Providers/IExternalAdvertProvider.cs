namespace Kinshout.ExternalImporter.Providers;

public interface IExternalAdvertProvider
{
    string Name { get; }
    Task<ProviderFetchResult> FetchAsync(CancellationToken ct);
}
