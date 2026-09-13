using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Kinshout.Api.Tests;

public class VideoServiceTests : IDisposable
{
    private readonly string _root;
    private readonly KinshoutDbContext _db;
    private readonly VideoService _service;
    private readonly Guid _userId;

    public VideoServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kinshout-video-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _db = TestDbFactory.Create();
        var (user, _) = TestDbFactory.SeedUserAndCategoryAsync(_db).GetAwaiter().GetResult();
        _userId = user.Id;

        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.ContentRootPath).Returns(_root);
        env.Setup(e => e.WebRootPath).Returns(Path.Combine(_root, "wwwroot"));

        var storage = new LocalUploadStorage(env.Object, Mock.Of<ILogger<LocalUploadStorage>>());
        _service = new VideoService(
            _db,
            storage,
            new AdvertImageProcessor(Microsoft.Extensions.Logging.Abstractions.NullLogger<AdvertImageProcessor>.Instance),
            Mock.Of<ILogger<VideoService>>());
    }

    [Fact]
    public async Task UploadAsync_StoresVideoAndReturnsPlayUrls()
    {
        var video = CreateFormFile("clip.mp4", "video/mp4", [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70]);

        var dto = await _service.UploadAsync(_userId, video);

        Assert.Equal(_userId, dto.OwnerId);
        Assert.Equal("video/mp4", dto.ContentType);
        Assert.StartsWith($"/uploads/videos/{_userId:N}/", dto.StorageUrl);
        Assert.EndsWith(".mp4", dto.StorageUrl);
        Assert.Equal($"/api/videos/{dto.Id}/stream", dto.PlayUrl);
        Assert.Equal($"/api/videos/{dto.Id}/preview", dto.PreviewUrl);

        var physical = Path.Combine(_root, "wwwroot", dto.StorageUrl.TrimStart('/'));
        Assert.True(File.Exists(physical));
    }

    [Fact]
    public async Task EnsureAssetsForStorageUrlsAsync_RegistersLegacyAndPreviewWorks()
    {
        var video = CreateFormFile("legacy.mp4", "video/mp4", [9, 8, 7, 6]);
        var uploaded = await _service.UploadAsync(_userId, video);
        var storageUrl = uploaded.StorageUrl;

        // Simulate legacy discussion media: storage file exists, no VideoAsset row.
        var existing = await _db.VideoAssets.SingleAsync(v => v.Id == uploaded.Id);
        _db.VideoAssets.Remove(existing);
        await _db.SaveChangesAsync();

        var map = await _service.EnsureAssetsForStorageUrlsAsync([storageUrl]);
        Assert.True(map.ContainsKey(storageUrl));

        var preview = await _service.OpenPreviewAsync(map[storageUrl].Id);
        Assert.NotNull(preview);
        await using (preview!.Stream)
        {
            Assert.True(preview.Stream.Length > 0);
            Assert.Equal("image/webp", preview.ContentType);
        }
    }

    [Fact]
    public async Task UploadAsync_WithPoster_CreatesCompressedPreview()
    {
        var video = CreateFormFile("clip.webm", "video/webm", [0x1A, 0x45, 0xDF, 0xA3]);
        var poster = CreateFormFile("poster.jpg", "image/jpeg", CreateJpegBytes());

        var dto = await _service.UploadAsync(_userId, video, poster);

        Assert.Equal("video/webm", dto.ContentType);
        Assert.Equal($"/api/videos/{dto.Id}/preview", dto.PreviewUrl);

        var preview = await _service.OpenPreviewAsync(dto.Id);
        Assert.NotNull(preview);
        await using (preview!.Stream)
        {
            Assert.True(preview.Stream.Length > 0);
            Assert.Equal("image/webp", preview.ContentType);
        }
    }

    [Fact]
    public async Task OpenStreamAsync_ReturnsVideoWithCorrectContentType()
    {
        var video = CreateFormFile("clip.mp4", "video/mp4", Enumerable.Repeat((byte)1, 2048).ToArray());
        var dto = await _service.UploadAsync(_userId, video);

        var stream = await _service.OpenStreamAsync(dto.Id);
        Assert.NotNull(stream);
        await using (stream!.Stream)
        {
            Assert.Equal("video/mp4", stream.ContentType);
            Assert.Equal(2048, stream.Stream.Length);
        }
    }

    [Fact]
    public async Task DeleteAsync_RemovesFilesAndSoftDeletes()
    {
        var video = CreateFormFile("clip.mp4", "video/mp4", [1, 2, 3, 4]);
        var poster = CreateFormFile("poster.png", "image/png", CreatePngBytes());
        var dto = await _service.UploadAsync(_userId, video, poster);
        var physical = Path.Combine(_root, "wwwroot", dto.StorageUrl.TrimStart('/'));
        Assert.True(File.Exists(physical));

        await _service.DeleteAsync(_userId, dto.Id);

        Assert.False(File.Exists(physical));
        Assert.Null(await _service.GetAsync(dto.Id));
        var row = await _db.VideoAssets.SingleAsync(v => v.Id == dto.Id);
        Assert.NotNull(row.DeletedAt);
    }

    [Fact]
    public async Task DeleteAsync_OtherUser_Throws()
    {
        var video = CreateFormFile("clip.mp4", "video/mp4", [1, 2, 3]);
        var dto = await _service.UploadAsync(_userId, video);
        var other = Guid.NewGuid();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.DeleteAsync(other, dto.Id));
    }

    [Fact]
    public async Task UploadAsync_RejectsOversizedVideo()
    {
        var huge = new byte[VideoService.MaxVideoBytes + 1];
        var video = CreateFormFile("big.mp4", "video/mp4", huge);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.UploadAsync(_userId, video));
        Assert.Contains("volumineuse", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static FormFile CreateFormFile(string name, string contentType, byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    private static byte[] CreateJpegBytes()
    {
        using var image = new Image<Rgba32>(32, 32);
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    private static byte[] CreatePngBytes()
    {
        using var image = new Image<Rgba32>(16, 16);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }
}
