namespace Kinshout.VideoWorker;

/// <summary>Queue message written by the API (<c>VideoProcessingJob</c>). Keep the JSON shape in sync.</summary>
public record VideoJob(
    Guid VideoId,
    string SourceUrl,
    string VideoUrl,
    string PosterUrl,
    double? TrimStartSeconds,
    double? TrimEndSeconds,
    double MaxDurationSeconds);

/// <summary>What ffprobe reports about a file.</summary>
public record MediaInfo(double DurationSeconds, int Width, int Height, bool HasAudio);

/// <summary>The part of the source that ends up in the published video.</summary>
public record TrimWindow(double StartSeconds, double DurationSeconds);

/// <summary>A failure retrying cannot fix (unreadable file, empty selection…).</summary>
public class PermanentVideoJobException(string message) : Exception(message);
