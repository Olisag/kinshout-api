using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Kinshout.VideoWorker;

// Runs as an Azure Container Apps job started by queue length: drains the queue, then exits.
var connectionString = Required("STORAGE_CONNECTION_STRING");
var apiBaseUrl = Required("API_BASE_URL");
var workerKey = Required("WORKER_KEY");
var containerName = Optional("STORAGE_CONTAINER", "uploads");
var queueName = Optional("QUEUE_NAME", "video-processing");
var maxJobs = int.Parse(Optional("MAX_JOBS", "10"));
const int maxAttempts = 3;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var queue = new QueueClient(
    connectionString,
    queueName,
    new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
await queue.CreateIfNotExistsAsync();

var processor = new VideoJobProcessor(
    new BlobContainerClient(connectionString, containerName),
    new FfmpegRunner(Optional("FFMPEG_PATH", "ffmpeg"), Optional("FFPROBE_PATH", "ffprobe")),
    new HttpClient { BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) },
    workerKey);

for (var processed = 0; processed < maxJobs; processed++)
{
    // Long enough for a two-minute 4K source on a small container; hidden from other executions meanwhile.
    QueueMessage? message = (await queue.ReceiveMessageAsync(TimeSpan.FromMinutes(20))).Value;
    if (message is null)
        break;

    VideoJob? job;
    try
    {
        job = JsonSerializer.Deserialize<VideoJob>(message.MessageText, jsonOptions);
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"Dropping unreadable message {message.MessageId}: {ex.Message}");
        await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt);
        continue;
    }

    if (job is null)
    {
        await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt);
        continue;
    }

    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(18));
    try
    {
        await processor.ProcessAsync(job, timeout.Token);
        await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt);
    }
    catch (Exception ex)
    {
        var permanent = ex is PermanentVideoJobException;
        Console.Error.WriteLine($"Video {job.VideoId} attempt {message.DequeueCount} failed: {ex.Message}");

        if (permanent || message.DequeueCount >= maxAttempts)
        {
            try
            {
                await processor.ReportFailureAsync(job.VideoId, ex.Message, CancellationToken.None);
            }
            catch (Exception reportError)
            {
                Console.Error.WriteLine($"Could not report failure for {job.VideoId}: {reportError.Message}");
            }

            await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt);
        }
        else
        {
            // Retry soon rather than after the full visibility timeout.
            await queue.UpdateMessageAsync(
                message.MessageId,
                message.PopReceipt,
                visibilityTimeout: TimeSpan.FromSeconds(30));
        }
    }
}

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing environment variable {name}.");

static string Optional(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
