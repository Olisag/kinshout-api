namespace Kinshout.ExternalImporter.Providers;

public interface IExternalAdvertProvider
{
    string Name { get; }
    Task<IReadOnlyList<SourceFeedAdvert>> FetchAsync(CancellationToken ct);
}
