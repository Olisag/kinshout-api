namespace Kinshout.Api.Configuration;

public class VideoProcessingSettings
{
    public const string SectionName = "VideoProcessing";

    /// <summary>
    /// When false, completed direct uploads are published as-is (no trim, no re-encode) so uploads
    /// keep working before the video worker is deployed.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Storage queue (in the upload storage account) the video worker listens on.</summary>
    public string QueueName { get; set; } = "video-processing";

    /// <summary>Shared secret the worker sends in <c>X-Video-Worker-Key</c> when reporting results.</summary>
    public string WorkerKey { get; set; } = string.Empty;

    public long MaxSourceBytes { get; set; } = 500L * 1024 * 1024;

    public double MaxDurationSeconds { get; set; } = 120;
}
