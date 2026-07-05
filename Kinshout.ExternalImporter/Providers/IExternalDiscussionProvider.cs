namespace Kinshout.ExternalImporter.Providers;

public interface IExternalDiscussionProvider
{
    string Name { get; }
    Task<DiscussionFetchResult> FetchAsync(CancellationToken ct);
}
