using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kinshout.VideoWorker;

internal sealed class FfmpegRunner(string ffmpegPath, string ffprobePath)
{
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
    {
        var (exitCode, output, error) = await RunAsync(
            ffprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
            ct);

        if (exitCode != 0)
            throw new PermanentVideoJobException($"ffprobe could not read the video: {LastLine(error)}");

        return ParseProbe(output);
    }

    public async Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var (exitCode, _, error) = await RunAsync(ffmpegPath, arguments, ct);
        if (exitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with code {exitCode}: {LastLine(error)}");
    }

    internal static MediaInfo ParseProbe(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        JsonElement? video = null;
        var hasAudio = false;

        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            var codecType = stream.TryGetProperty("codec_type", out var type) ? type.GetString() : null;
            if (codecType == "video" && video is null && !IsAttachedPicture(stream))
                video = stream;
            else if (codecType == "audio")
                hasAudio = true;
        }

        if (video is null)
            throw new PermanentVideoJobException("The file has no video track.");

        var width = video.Value.GetProperty("width").GetInt32();
        var height = video.Value.GetProperty("height").GetInt32();
        if (Math.Abs(GetRotation(video.Value)) % 180 == 90)
            (width, height) = (height, width);

        var duration = root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durationValue) &&
            double.TryParse(durationValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;

        return new MediaInfo(duration, width, height, hasAudio);
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition) &&
        disposition.TryGetProperty("attached_pic", out var attached) &&
        attached.GetInt32() == 1;

    private static int GetRotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData))
        {
            foreach (var item in sideData.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var rotation) && rotation.TryGetInt32(out var degrees))
                    return degrees;
            }
        }

        if (stream.TryGetProperty("tags", out var tags) &&
            tags.TryGetProperty("rotate", out var rotate) &&
            int.TryParse(rotate.GetString(), out var tagDegrees))
        {
            return tagDegrees;
        }

        return 0;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");

        var output = new StringBuilder();
        var error = new StringBuilder();
        var outputTask = PumpAsync(process.StandardOutput, output);
        var errorTask = PumpAsync(process.StandardError, error);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        await Task.WhenAll(outputTask, errorTask);
        return (process.ExitCode, output.ToString(), error.ToString());
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder target)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            // ffmpeg logs a lot; only the tail matters for error messages.
            if (target.Length > 64 * 1024)
                target.Remove(0, target.Length - 16 * 1024);
            target.Append(buffer, 0, read);
        }
    }

    private static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "no output" : lines[^1];
    }
}
