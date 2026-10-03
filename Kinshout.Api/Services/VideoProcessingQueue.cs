using System.Text.Json;
using Azure.Storage.Queues;
using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

/// <summary>
/// Message read by the video worker (Kinshout.VideoWorker). Keep the JSON shape in sync with
/// <c>VideoJob</c> in the worker.
/// </summary>
public record VideoProcessingJob(
    Guid VideoId,
    string SourceUrl,
    string VideoUrl,
    string PosterUrl,
    double? TrimStartSeconds,
    double? TrimEndSeconds,
    double MaxDurationSeconds);

public interface IVideoProcessingQueue
{
    Task EnqueueAsync(VideoProcessingJob job, CancellationToken ct = default);
}

public sealed class AzureVideoProcessingQueue(
    IOptions<UploadStorageSettings> storageOptions,
    IOptions<VideoProcessingSettings> processingOptions) : IVideoProcessingQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private QueueClient? _queue;

    public async Task EnqueueAsync(VideoProcessingJob job, CancellationToken ct = default)
    {
        var queue = await GetQueueAsync(ct);
        await queue.SendMessageAsync(JsonSerializer.Serialize(job, JsonOptions), ct);
    }

    private async Task<QueueClient> GetQueueAsync(CancellationToken ct)
    {
        if (_queue is not null)
            return _queue;

        var queue = new QueueClient(
            storageOptions.Value.AzureBlobConnectionString,
            processingOptions.Value.QueueName,
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        await queue.CreateIfNotExistsAsync(cancellationToken: ct);
        _queue = queue;
        return queue;
    }
}
