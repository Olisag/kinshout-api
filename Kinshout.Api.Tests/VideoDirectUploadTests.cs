using Kinshout.Api.Configuration;
using Kinshout.Api.Controllers;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public class VideoDirectUploadTests : IDisposable
{
    private readonly string _root;
    private readonly KinshoutDbContext _db;
    private readonly LocalUploadStorage _localStorage;
    private readonly Mock<IVideoProcessingQueue> _queue = new();
    private readonly Guid _userId;

    public VideoDirectUploadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kinshout-video-upload-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _db = TestDbFactory.Create();
        var (user, _) = TestDbFactory.SeedUserAndCategoryAsync(_db).GetAwaiter().GetResult();
        _userId = user.Id;

        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.ContentRootPath).Returns(_root);
        env.Setup(e => e.WebRootPath).Returns(Path.Combine(_root, "wwwroot"));
        _localStorage = new LocalUploadStorage(env.Object, Mock.Of<ILogger<LocalUploadStorage>>());
    }

    [Fact]
    public async Task CreateUpload_ReturnsNull_WhenStorageCannotIssueUploadUrls()
    {
        var service = CreateService(_localStorage);

        var upload = await service.CreateUploadAsync(_userId, new("clip.mp4", "video/mp4", 1024));

        Assert.Null(upload);
        Assert.Empty(_db.VideoAssets);
    }

    [Fact]
    public async Task CreateUpload_RegistersUploadingAssetWithSourceAndFinalPaths()
    {
        var service = CreateService(new DirectUploadStorage(_localStorage));

        var upload = await service.CreateUploadAsync(_userId, new("Holiday.MOV", "video/quicktime", 1024));

        Assert.NotNull(upload);
        Assert.StartsWith("https://storage.example/uploads/videos/", upload.UploadUrl);
        Assert.Equal(VideoAssetStatus.Uploading, upload.Video.Status);
        Assert.EndsWith(".mp4", upload.Video.StorageUrl);

        var asset = Assert.Single(_db.VideoAssets);
        Assert.EndsWith("_source.mov", asset.SourceUrl);
        Assert.Equal($"/uploads/videos/{_userId:N}/", asset.VideoUrl[..(asset.VideoUrl.LastIndexOf('/') + 1)]);
    }

    [Theory]
    [InlineData("clip.mp4", 600L * 1024 * 1024)]
    [InlineData("clip.avi", 1024)]
    [InlineData("clip.mp4", 0)]
    public async Task CreateUpload_RejectsOversizedOrUnsupportedFiles(string fileName, long byteSize)
    {
        var service = CreateService(new DirectUploadStorage(_localStorage));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateUploadAsync(_userId, new(fileName, null, byteSize)));
    }

    [Fact]
    public async Task Complete_PublishesSourceAsIs_WhenProcessingIsDisabled()
    {
        var service = CreateService(new DirectUploadStorage(_localStorage), enabled: false);
        var upload = await CreateUploadedVideoAsync(service);

        var dto = await service.CompleteUploadAsync(_userId, upload.Id, 10, 70);

        Assert.Equal(VideoAssetStatus.Ready, dto.Status);
        Assert.EndsWith("_source.mp4", dto.StorageUrl);
        Assert.Null(Assert.Single(_db.VideoAssets).SourceUrl);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<VideoProcessingJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Complete_QueuesProcessingOnce_WithTrimRange()
    {
        var service = CreateService(new DirectUploadStorage(_localStorage));
        var upload = await CreateUploadedVideoAsync(service);

        var dto = await service.CompleteUploadAsync(_userId, upload.Id, 30, 150);
        var retried = await service.CompleteUploadAsync(_userId, upload.Id, 30, 150);

        Assert.Equal(VideoAssetStatus.Processing, dto.Status);
        Assert.Equal(VideoAssetStatus.Processing, retried.Status);
        _queue.Verify(q => q.EnqueueAsync(
                It.Is<VideoProcessingJob>(job =>
                    job.VideoId == upload.Id &&
                    job.SourceUrl.EndsWith("_source.mp4") &&
                    job.VideoUrl == upload.Video.StorageUrl &&
                    job.PosterUrl.EndsWith("_poster.jpg") &&
                    job.TrimStartSeconds == 30 &&
                    job.TrimEndSeconds == 150 &&
                    job.MaxDurationSeconds == 120),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Complete_PublishesSourceAsIs_WhenQueueingFails()
    {
        _queue.Setup(q => q.EnqueueAsync(It.IsAny<VideoProcessingJob>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("queue down"));
        var service = CreateService(new DirectUploadStorage(_localStorage));
        var upload = await CreateUploadedVideoAsync(service);

        var dto = await service.CompleteUploadAsync(_userId, upload.Id, null, null);

        Assert.Equal(VideoAssetStatus.Ready, dto.Status);
    }

    [Theory]
    [InlineData(0, 125)]
    [InlineData(50, 40)]
    [InlineData(-1, 10)]
    public async Task Complete_RejectsInvalidTrimRange(double start, double end)
    {
        var service = CreateService(new DirectUploadStorage(_localStorage));
        var upload = await CreateUploadedVideoAsync(service);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CompleteUploadAsync(_userId, upload.Id, start, end));
    }

    [Fact]
    public async Task Complete_Throws_WhenSourceWasNeverUploaded()
    {
        var service = CreateService(new DirectUploadStorage(_localStorage));
        var upload = await service.CreateUploadAsync(_userId, new("clip.mp4", "video/mp4", 1024));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CompleteUploadAsync(_userId, upload!.Id, null, null));
    }

    [Fact]
    public async Task Complete_RejectsOtherUsers()
    {
        var service = CreateService(new DirectUploadStorage(_localStorage));
        var upload = await CreateUploadedVideoAsync(service);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CompleteUploadAsync(Guid.NewGuid(), upload.Id, null, null));
    }

    [Fact]
    public async Task ProcessingSuccess_MakesVideoReadyAndCleansUp()
    {
        var service = CreateService(new DirectUploadStorage(_localStorage));
        var upload = await CreateUploadedVideoAsync(service);
        await service.CompleteUploadAsync(_userId, upload.Id, null, null);
        Assert.Null(await service.GetDirectStreamUriAsync(upload.Id));

        var asset = Assert.Single(_db.VideoAssets);
        var sourceUrl = asset.SourceUrl!;
        WriteStoredFile(asset.VideoUrl, new byte[2048]);
        var posterUrl = asset.VideoUrl.Replace(".mp4", "_poster.jpg");
        WriteStoredFile(posterUrl, [1, 2, 3]);

        var applied = await service.ApplyProcessingResultAsync(
            upload.Id,
            new VideoProcessingResultDto(true, DurationSeconds: 95.5, Width: 720, Height: 1280));
        var dto = await service.GetAsync(upload.Id);

        Assert.True(applied);
        Assert.NotNull(dto);
        Assert.Equal(VideoAssetStatus.Ready, dto.Status);
        Assert.Equal(2048, dto.ByteSize);
        Assert.Equal(95.5, dto.DurationSeconds);
        Assert.Equal(720, dto.Width);
        Assert.Equal(1280, dto.Height);
        Assert.Equal(posterUrl, asset.PosterUrl);
        Assert.False(await _localStorage.ExistsAsync(sourceUrl));
        Assert.NotNull(await service.OpenStreamAsync(upload.Id));
    }

    [Fact]
    public async Task ProcessingFailure_MarksVideoFailed()
    {
        var service = CreateService(new DirectUploadStorage(_localStorage));
        var upload = await CreateUploadedVideoAsync(service);
        await service.CompleteUploadAsync(_userId, upload.Id, null, null);

        await service.ApplyProcessingResultAsync(
            upload.Id,
            new VideoProcessingResultDto(false, Error: "ffmpeg exited with code 1"));
        var dto = await service.GetAsync(upload.Id);

        Assert.Equal(VideoAssetStatus.Failed, dto!.Status);
        Assert.Equal("ffmpeg exited with code 1", dto.Error);
        Assert.Null(await service.OpenStreamAsync(upload.Id));
    }

    [Fact]
    public async Task ProcessingSuccess_WithoutProcessedFile_MarksVideoFailed()
    {
        var service = CreateService(new DirectUploadStorage(_localStorage));
        var upload = await CreateUploadedVideoAsync(service);
        await service.CompleteUploadAsync(_userId, upload.Id, null, null);

        await service.ApplyProcessingResultAsync(upload.Id, new VideoProcessingResultDto(true));

        Assert.Equal(VideoAssetStatus.Failed, (await service.GetAsync(upload.Id))!.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task ProcessingResultEndpoint_RejectsRequestsWithoutTheWorkerKey(string? key)
    {
        var videos = new Mock<IVideoService>();
        var controller = CreateController(videos.Object, key);

        var result = await controller.ProcessingResult(Guid.NewGuid(), new(true), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        videos.Verify(v => v.ApplyProcessingResultAsync(
            It.IsAny<Guid>(), It.IsAny<VideoProcessingResultDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessingResultEndpoint_AppliesResult_WithTheWorkerKey()
    {
        var id = Guid.NewGuid();
        var videos = new Mock<IVideoService>();
        videos.Setup(v => v.ApplyProcessingResultAsync(id, It.IsAny<VideoProcessingResultDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var controller = CreateController(videos.Object, "worker-secret");

        var result = await controller.ProcessingResult(id, new(true), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteAbandonedUploads_OnlyDeletesOldUploadsThatWereNeverCompleted()
    {
        var service = CreateService(new DirectUploadStorage(_localStorage));
        var abandoned = await CreateUploadedVideoAsync(service);
        var completed = await service.CreateUploadAsync(_userId, new("done.mp4", "video/mp4", 1024));
        var recent = await service.CreateUploadAsync(_userId, new("recent.mp4", "video/mp4", 1024));
        var completedAsset = await _db.VideoAssets.SingleAsync(v => v.Id == completed!.Id);
        WriteStoredFile(completedAsset.SourceUrl!, new byte[1024]);
        await service.CompleteUploadAsync(_userId, completed!.Id, null, null);

        foreach (var asset in _db.VideoAssets.Where(v => v.Id != recent!.Id))
            asset.CreatedAt = DateTime.UtcNow.AddDays(-2);
        await _db.SaveChangesAsync();

        var abandonedSource = (await _db.VideoAssets.SingleAsync(v => v.Id == abandoned.Id)).SourceUrl!;
        var deleted = await service.DeleteAbandonedUploadsAsync(TimeSpan.FromDays(1));

        Assert.Equal(1, deleted);
        Assert.NotNull((await _db.VideoAssets.SingleAsync(v => v.Id == abandoned.Id)).DeletedAt);
        Assert.False(await _localStorage.ExistsAsync(abandonedSource));
        Assert.Null((await _db.VideoAssets.SingleAsync(v => v.Id == completed.Id)).DeletedAt);
        Assert.Null((await _db.VideoAssets.SingleAsync(v => v.Id == recent!.Id)).DeletedAt);
    }

    private VideoService CreateService(IUploadStorage storage, bool enabled = true) =>
        new(
            _db,
            storage,
            new AdvertImageProcessor(NullLogger<AdvertImageProcessor>.Instance),
            Mock.Of<ILogger<VideoService>>(),
            _queue.Object,
            Options.Create(new VideoProcessingSettings { Enabled = enabled }));

    private async Task<VideoUploadDto> CreateUploadedVideoAsync(VideoService service)
    {
        var upload = await service.CreateUploadAsync(_userId, new("clip.mp4", "video/mp4", 1024));
        Assert.NotNull(upload);
        WriteStoredFile(Assert.Single(_db.VideoAssets).SourceUrl!, new byte[1024]);
        return upload;
    }

    private void WriteStoredFile(string uploadUrl, byte[] content)
    {
        var path = Path.Combine(_root, "wwwroot", uploadUrl.TrimStart('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static VideosController CreateController(IVideoService videos, string? providedKey)
    {
        var context = new DefaultHttpContext();
        if (providedKey is not null)
            context.Request.Headers[VideosController.WorkerKeyHeader] = providedKey;

        return new VideosController(
            videos,
            Options.Create(new VideoProcessingSettings { WorkerKey = "worker-secret" }))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Local storage that also hands out upload URLs, like Azure Blob does.</summary>
    private sealed class DirectUploadStorage(LocalUploadStorage inner) : IUploadStorage
    {
        public Task<string> SaveAsync(string folder, Guid userId, Stream stream, string extension, CancellationToken ct = default) =>
            inner.SaveAsync(folder, userId, stream, extension, ct);

        public Task<string> SaveNamedAsync(string folder, Guid userId, Stream stream, string fileName, CancellationToken ct = default) =>
            inner.SaveNamedAsync(folder, userId, stream, fileName, ct);

        public Task<UploadFileContent?> OpenReadAsync(string uploadUrl, CancellationToken ct = default) =>
            inner.OpenReadAsync(uploadUrl, ct);

        public Task DeleteIfExistsAsync(string uploadUrl, CancellationToken ct = default) =>
            inner.DeleteIfExistsAsync(uploadUrl, ct);

        public Task<bool> ExistsAsync(string uploadUrl, CancellationToken ct = default) =>
            inner.ExistsAsync(uploadUrl, ct);

        public Task<long?> GetLengthAsync(string uploadUrl, CancellationToken ct = default) =>
            inner.GetLengthAsync(uploadUrl, ct);

        public Task<Uri?> GetDirectWriteUriAsync(string uploadUrl, TimeSpan lifetime, CancellationToken ct = default) =>
            Task.FromResult<Uri?>(new Uri($"https://storage.example{uploadUrl}?sig=write"));
    }
}
