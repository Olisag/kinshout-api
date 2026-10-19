using Kinshout.VideoWorker;

namespace Kinshout.Api.Tests;

public class VideoWorkerPlanTests
{
    [Theory]
    [InlineData(null, null, 300, 0, 120)]   // untrimmed long video keeps its first two minutes
    [InlineData(null, null, 45, 0, 45)]     // short video is kept whole
    [InlineData(30.0, 150.0, 300, 30, 120)] // user selection
    [InlineData(30.0, 200.0, 300, 30, 120)] // selection longer than allowed is capped
    [InlineData(250.0, 400.0, 300, 250, 50)] // selection past the end stops at the end
    [InlineData(-5.0, 20.0, 300, 0, 20)]
    public void TrimWindow_StaysInsideTheVideoAndTheLimit(
        double? start,
        double? end,
        double sourceDuration,
        double expectedStart,
        double expectedDuration)
    {
        var window = VideoEncodingPlan.GetTrimWindow(Job(start, end), sourceDuration);

        Assert.Equal(expectedStart, window.StartSeconds, 3);
        Assert.Equal(expectedDuration, window.DurationSeconds, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(299.8)]
    public void TrimWindow_RejectsEmptySelections(double start)
    {
        var job = Job(start, null);

        Assert.Throws<PermanentVideoJobException>(() =>
            VideoEncodingPlan.GetTrimWindow(job, start == 0 ? 0 : 300));
    }

    [Fact]
    public void EncodeArguments_TrimScaleAndCompress()
    {
        var arguments = VideoEncodingPlan.GetEncodeArguments(
            "in.mov",
            "out.mp4",
            new TrimWindow(12.5, 120),
            hasAudio: true);

        Assert.Equal("12.5", ArgumentAfter(arguments, "-ss"));
        Assert.Equal("120", ArgumentAfter(arguments, "-t"));
        Assert.True(arguments.ToList().IndexOf("-ss") < arguments.ToList().IndexOf("-i"));
        Assert.Equal("libx264", ArgumentAfter(arguments, "-c:v"));
        Assert.Equal("aac", ArgumentAfter(arguments, "-c:a"));
        Assert.Equal("+faststart", ArgumentAfter(arguments, "-movflags"));
        Assert.Contains("min(720,ih)", ArgumentAfter(arguments, "-vf"));
        Assert.Equal("out.mp4", arguments[^1]);
    }

    [Fact]
    public void EncodeArguments_SkipAudioOptions_ForSilentVideos()
    {
        var arguments = VideoEncodingPlan.GetEncodeArguments("in.mp4", "out.mp4", new TrimWindow(0, 10), hasAudio: false);

        Assert.DoesNotContain("-c:a", arguments);
        Assert.DoesNotContain("0:a:0", arguments);
    }

    [Fact]
    public void CanCopy_VideosAlreadyCompressedOnTheDevice()
    {
        Assert.True(VideoEncodingPlan.CanCopy(DeviceCompressed(), new TrimWindow(0, 60)));
    }

    [Fact]
    public void CanCopy_SafariFullRangeVideos()
    {
        var safari = DeviceCompressed() with { Width = 576, Height = 1024, PixelFormat = "yuvj420p", FrameRate = 27.3 };

        Assert.True(VideoEncodingPlan.CanCopy(safari, new TrimWindow(0, 60)));
    }

    [Fact]
    public void CanCopy_SilentVideos()
    {
        var silent = DeviceCompressed() with { HasAudio = false, AudioCodec = null };

        Assert.True(VideoEncodingPlan.CanCopy(silent, new TrimWindow(0, 60)));
    }

    public static TheoryData<MediaInfo> VideosThatNeedEncoding() => new()
    {
        DeviceCompressed() with { Width = 1080, Height = 1920 },
        DeviceCompressed() with { VideoCodec = "hevc" },
        DeviceCompressed() with { PixelFormat = "yuv420p10le" },
        DeviceCompressed() with { FrameRate = 60 },
        DeviceCompressed() with { FrameRate = 0 },
        DeviceCompressed() with { BitRate = 12_000_000 },
        DeviceCompressed() with { BitRate = 0 },
        DeviceCompressed() with { AudioCodec = "opus" },
    };

    [Theory]
    [MemberData(nameof(VideosThatNeedEncoding))]
    public void CanCopy_RejectsVideosTheEncodeWouldChange(MediaInfo source)
    {
        Assert.False(VideoEncodingPlan.CanCopy(source, new TrimWindow(0, 60)));
    }

    [Theory]
    [InlineData(5, 55)]   // starts later
    [InlineData(0, 40)]   // ends earlier
    public void CanCopy_RejectsVideosThatStillNeedCutting(double start, double duration)
    {
        Assert.False(VideoEncodingPlan.CanCopy(DeviceCompressed(), new TrimWindow(start, duration)));
    }

    [Fact]
    public void CopyArguments_RemuxWithoutReencoding()
    {
        var arguments = VideoEncodingPlan.GetCopyArguments("in.mp4", "out.mp4", hasAudio: true);

        Assert.Equal("copy", ArgumentAfter(arguments, "-c"));
        Assert.Contains("0:a:0", arguments);
        Assert.Equal("+faststart", ArgumentAfter(arguments, "-movflags"));
        Assert.DoesNotContain("-vf", arguments);
        Assert.Equal("out.mp4", arguments[^1]);
    }

    [Fact]
    public void ProbeParsing_ReadsWhatDecidesBetweenCopyAndEncode()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "h264", "pix_fmt": "yuv420p",
                  "width": 720, "height": 1280, "avg_frame_rate": "30000/1001", "r_frame_rate": "30/1" },
                { "codec_type": "audio", "codec_name": "aac" }
              ],
              "format": { "duration": "60.0", "bit_rate": "2100000" }
            }
            """;

        var info = FfmpegRunner.ParseProbe(json);

        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal("yuv420p", info.PixelFormat);
        Assert.Equal(29.97, info.FrameRate, 2);
        Assert.Equal(2_100_000, info.BitRate);
        Assert.Equal("aac", info.AudioCodec);
    }

    [Fact]
    public void ProbeParsing_FallsBackToTheNominalFrameRate()
    {
        const string json = """
            {
              "streams": [ { "codec_type": "video", "width": 720, "height": 1280,
                             "avg_frame_rate": "0/0", "r_frame_rate": "30/1" } ],
              "format": { "duration": "10" }
            }
            """;

        Assert.Equal(30, FfmpegRunner.ParseProbe(json).FrameRate, 3);
    }

    [Fact]
    public void ProbeParsing_SwapsDimensionsForRotatedPhoneVideos()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "width": 1920, "height": 1080,
                  "side_data_list": [ { "side_data_type": "Display Matrix", "rotation": -90 } ] },
                { "codec_type": "audio" }
              ],
              "format": { "duration": "184.533" }
            }
            """;

        var info = FfmpegRunner.ParseProbe(json);

        Assert.Equal(1080, info.Width);
        Assert.Equal(1920, info.Height);
        Assert.Equal(184.533, info.DurationSeconds, 3);
        Assert.True(info.HasAudio);
    }

    [Fact]
    public void ProbeParsing_RejectsFilesWithoutVideo()
    {
        const string json = """{ "streams": [ { "codec_type": "audio" } ], "format": { "duration": "10" } }""";

        Assert.Throws<PermanentVideoJobException>(() => FfmpegRunner.ParseProbe(json));
    }

    [Fact]
    public void BlobNames_ComeFromUploadPaths()
    {
        Assert.Equal("videos/abc/file.mp4", VideoJobProcessor.ToBlobName("/uploads/videos/abc/file.mp4"));
        Assert.Throws<PermanentVideoJobException>(() => VideoJobProcessor.ToBlobName("https://elsewhere/file.mp4"));
    }

    private static MediaInfo DeviceCompressed() =>
        new(60, 720, 1280, HasAudio: true, "h264", "yuv420p", FrameRate: 30, BitRate: 2_100_000, "aac");

    private static VideoJob Job(double? start, double? end) =>
        new(Guid.NewGuid(), "/uploads/videos/u/a_source.mp4", "/uploads/videos/u/a.mp4", "/uploads/videos/u/a_poster.jpg", start, end, 120);

    private static string ArgumentAfter(IReadOnlyList<string> arguments, string name) =>
        arguments[arguments.ToList().IndexOf(name) + 1];
}
