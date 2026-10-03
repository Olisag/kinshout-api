using System.Globalization;

namespace Kinshout.VideoWorker;

/// <summary>Pure decisions about a job: which part of the video to keep and how to encode it.</summary>
internal static class VideoEncodingPlan
{
    public const int MaxShortSide = 720;
    public const int PosterMaxSide = 640;
    private const double MinimumDurationSeconds = 0.5;
    private const long MaxCopyBitRate = 3_500_000;
    private const double MaxCopyFrameRate = 31;
    private const double CopyTrimToleranceSeconds = 0.25;

    public static TrimWindow GetTrimWindow(VideoJob job, double sourceDurationSeconds)
    {
        if (sourceDurationSeconds <= 0)
            throw new PermanentVideoJobException("The video has no duration.");

        var start = Math.Clamp(job.TrimStartSeconds ?? 0, 0, sourceDurationSeconds);
        var end = Math.Min(job.TrimEndSeconds ?? sourceDurationSeconds, sourceDurationSeconds);
        // Longer selections (or untrimmed long videos) keep their first allowed minutes.
        end = Math.Min(end, start + job.MaxDurationSeconds);

        if (end - start < MinimumDurationSeconds)
            throw new PermanentVideoJobException("The selected part of the video is too short.");

        return new TrimWindow(start, end - start);
    }

    /// <summary>
    /// H.264/AAC MP4 that plays everywhere: short side at most 720 px (never upscaled), at most
    /// 30 fps, bitrate capped for mobile data, moov atom first so playback starts before the download ends.
    /// ffmpeg applies the rotation metadata of phone videos before these filters run.
    /// </summary>
    public static IReadOnlyList<string> GetEncodeArguments(
        string inputPath,
        string outputPath,
        TrimWindow window,
        bool hasAudio)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-ss", Seconds(window.StartSeconds),
            "-i", inputPath,
            "-t", Seconds(window.DurationSeconds),
            "-map", "0:v:0",
        };

        if (hasAudio)
            arguments.AddRange(["-map", "0:a:0"]);

        arguments.AddRange(
        [
            "-vf", ScaleFilter(MaxShortSide, shortSide: true),
            "-fpsmax", "30",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "26",
            "-maxrate", "2500k",
            "-bufsize", "5000k",
            "-profile:v", "high",
            "-pix_fmt", "yuv420p",
        ]);

        if (hasAudio)
            arguments.AddRange(["-c:a", "aac", "-b:a", "96k", "-ac", "2"]);

        arguments.AddRange(["-map_metadata", "-1", "-movflags", "+faststart", outputPath]);
        return arguments;
    }

    /// <summary>
    /// Browsers that can encode compress to 720p H.264 before uploading: when the file already matches
    /// what <see cref="GetEncodeArguments"/> would produce and nothing has to be cut, a remux is enough.
    /// </summary>
    public static bool CanCopy(MediaInfo source, TrimWindow window) =>
        window.StartSeconds <= CopyTrimToleranceSeconds &&
        window.DurationSeconds >= source.DurationSeconds - CopyTrimToleranceSeconds &&
        source.VideoCodec == "h264" &&
        source.PixelFormat == "yuv420p" &&
        Math.Min(source.Width, source.Height) <= MaxShortSide &&
        source.FrameRate is > 0 and <= MaxCopyFrameRate &&
        source.BitRate is > 0 and <= MaxCopyBitRate &&
        (!source.HasAudio || source.AudioCodec == "aac");

    public static IReadOnlyList<string> GetCopyArguments(string inputPath, string outputPath, bool hasAudio)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", inputPath, "-map", "0:v:0" };

        if (hasAudio)
            arguments.AddRange(["-map", "0:a:0"]);

        arguments.AddRange(["-c", "copy", "-map_metadata", "-1", "-movflags", "+faststart", outputPath]);
        return arguments;
    }

    public static IReadOnlyList<string> GetPosterArguments(string videoPath, string posterPath, double durationSeconds) =>
    [
        "-hide_banner", "-nostdin", "-y",
        // Skip the first instant, which is often a black or fading frame.
        "-ss", Seconds(Math.Min(1, durationSeconds * 0.25)),
        "-i", videoPath,
        "-frames:v", "1",
        "-vf", ScaleFilter(PosterMaxSide, shortSide: false),
        "-q:v", "4",
        posterPath,
    ];

    /// <summary>Limits the short (or long) side of the picture, keeping even dimensions for H.264.</summary>
    internal static string ScaleFilter(int maxSide, bool shortSide)
    {
        var limitedWhenLandscape = shortSide ? "h" : "w";
        var max = maxSide.ToString(CultureInfo.InvariantCulture);
        var width = limitedWhenLandscape == "w"
            ? $"if(gte(iw,ih),trunc(min({max},iw)/2)*2,-2)"
            : $"if(gte(iw,ih),-2,trunc(min({max},iw)/2)*2)";
        var height = limitedWhenLandscape == "w"
            ? $"if(gte(iw,ih),-2,trunc(min({max},ih)/2)*2)"
            : $"if(gte(iw,ih),trunc(min({max},ih)/2)*2,-2)";

        return $"scale=w='{width}':h='{height}'";
    }

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
