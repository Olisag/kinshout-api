using System.Net.Http.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Kinshout.VideoWorker;

internal sealed class VideoJobProcessor(
    BlobContainerClient container,
    FfmpegRunner ffmpeg,
    HttpClient api,
    string workerKey)
{
    private const string CacheControl = "public,max-age=31536000,immutable";

    public async Task ProcessAsync(VideoJob job, CancellationToken ct)
    {
        var workDirectory = Path.Combine(Path.GetTempPath(), $"video-{job.VideoId:N}");
        Directory.CreateDirectory(workDirectory);

        try
        {
            var sourcePath = Path.Combine(workDirectory, "source" + Path.GetExtension(job.SourceUrl));
            var outputPath = Path.Combine(workDirectory, "output.mp4");
            var posterPath = Path.Combine(workDirectory, "poster.jpg");

            var source = container.GetBlobClient(ToBlobName(job.SourceUrl));
            if (!await source.ExistsAsync(ct))
                throw new PermanentVideoJobException("The uploaded video is missing from storage.");

            await source.DownloadToAsync(sourcePath, ct);

            var sourceInfo = await ffmpeg.ProbeAsync(sourcePath, ct);
            var window = VideoEncodingPlan.GetTrimWindow(job, sourceInfo.DurationSeconds);
            var copy = VideoEncodingPlan.CanCopy(sourceInfo, window);

            await ffmpeg.RunFfmpegAsync(
                copy
                    ? VideoEncodingPlan.GetCopyArguments(sourcePath, outputPath, sourceInfo.HasAudio)
                    : VideoEncodingPlan.GetEncodeArguments(sourcePath, outputPath, window, sourceInfo.HasAudio),
                ct);

            var outputInfo = await ffmpeg.ProbeAsync(outputPath, ct);
            await ffmpeg.RunFfmpegAsync(
                VideoEncodingPlan.GetPosterArguments(outputPath, posterPath, outputInfo.DurationSeconds),
                ct);

            await UploadAsync(outputPath, job.VideoUrl, "video/mp4", ct);
            await UploadAsync(posterPath, job.PosterUrl, "image/jpeg", ct);

            await ReportAsync(
                job.VideoId,
                new ProcessingResult(
                    true,
                    DurationSeconds: Math.Round(outputInfo.DurationSeconds, 2),
                    Width: outputInfo.Width,
                    Height: outputInfo.Height),
                ct);

            Console.WriteLine(
                $"Processed {job.VideoId} ({(copy ? "copied" : "encoded")}): " +
                $"{window.DurationSeconds:0.#}s from {window.StartSeconds:0.#}s, " +
                $"{outputInfo.Width}x{outputInfo.Height}, {new FileInfo(outputPath).Length} bytes");
        }
        finally
        {
            try
            {
                Directory.Delete(workDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    public Task ReportFailureAsync(Guid videoId, string error, CancellationToken ct) =>
        ReportAsync(videoId, new ProcessingResult(false, Error: error), ct);

    private async Task UploadAsync(string path, string uploadUrl, string contentType, CancellationToken ct)
    {
        var blob = container.GetBlobClient(ToBlobName(uploadUrl));
        await blob.UploadAsync(
            path,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType, CacheControl = CacheControl },
            },
            ct);
    }

    private async Task ReportAsync(Guid videoId, ProcessingResult result, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/videos/{videoId}/processing-result")
        {
            Content = JsonContent.Create(result),
        };
        request.Headers.Add("X-Video-Worker-Key", workerKey);

        using var response = await api.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>"/uploads/videos/{user}/{file}" → "videos/{user}/{file}" in the uploads container.</summary>
    internal static string ToBlobName(string uploadUrl)
    {
        const string prefix = "/uploads/";
        if (!uploadUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new PermanentVideoJobException($"Unexpected storage path: {uploadUrl}");

        return uploadUrl[prefix.Length..];
    }

    private record ProcessingResult(
        bool Succeeded,
        double? DurationSeconds = null,
        int? Width = null,
        int? Height = null,
        string? Error = null);
}
