using System.Diagnostics;
using Kinshout.VideoWorker;

namespace Kinshout.Api.Tests;

/// <summary>Runs the real ffmpeg pipeline; does nothing on machines without ffmpeg.</summary>
public class VideoWorkerFfmpegTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "kinshout-ffmpeg-tests", Guid.NewGuid().ToString("N"));

    public VideoWorkerFfmpegTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task LongRotatedPhoneVideo_IsCappedScaledAndUpright()
    {
        if (!IsOnPath("ffmpeg") || !IsOnPath("ffprobe"))
            return;

        var landscape = Path.Combine(_directory, "landscape.mp4");
        var source = Path.Combine(_directory, "source.mp4");
        var output = Path.Combine(_directory, "output.mp4");
        var poster = Path.Combine(_directory, "poster.jpg");
        // 150 s landscape 1920x1080 with audio, then flagged as shot in portrait like phone videos.
        await RunAsync(
            "ffmpeg",
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30:duration=150",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=150",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac",
            landscape);
        await RunAsync(
            "ffmpeg",
            "-hide_banner", "-y",
            "-display_rotation:v:0", "90",
            "-i", landscape,
            "-c", "copy",
            source);

        var ffmpeg = new FfmpegRunner("ffmpeg", "ffprobe");
        var sourceInfo = await ffmpeg.ProbeAsync(source, CancellationToken.None);
        var job = new VideoJob(Guid.NewGuid(), "", "", "", TrimStartSeconds: 10, TrimEndSeconds: null, 120);
        var window = VideoEncodingPlan.GetTrimWindow(job, sourceInfo.DurationSeconds);

        await ffmpeg.RunFfmpegAsync(
            VideoEncodingPlan.GetEncodeArguments(source, output, window, sourceInfo.HasAudio),
            CancellationToken.None);
        var outputInfo = await ffmpeg.ProbeAsync(output, CancellationToken.None);
        await ffmpeg.RunFfmpegAsync(
            VideoEncodingPlan.GetPosterArguments(output, poster, outputInfo.DurationSeconds),
            CancellationToken.None);

        Assert.Equal(1080, sourceInfo.Width);
        Assert.Equal(1920, sourceInfo.Height);
        Assert.InRange(outputInfo.DurationSeconds, 119.5, 120.5);
        Assert.Equal(720, outputInfo.Width);
        Assert.Equal(1280, outputInfo.Height);
        Assert.True(outputInfo.HasAudio);
        Assert.True(new FileInfo(poster).Length > 0);
    }

    private static bool IsOnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Any(directory => File.Exists(Path.Combine(directory, tool)));

    private static async Task RunAsync(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
