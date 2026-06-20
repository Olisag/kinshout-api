using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace Kinshout.Api.Tests;

public class UploadServiceTests : IDisposable
{
    private readonly string _root;
    private readonly UploadService _service;
    private readonly Guid _userId = Guid.NewGuid();

    public UploadServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kinshout-upload-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.ContentRootPath).Returns(_root);
        env.Setup(e => e.WebRootPath).Returns(Path.Combine(_root, "wwwroot"));

        var storage = new LocalUploadStorage(env.Object, Mock.Of<ILogger<LocalUploadStorage>>());

        var moderation = new Mock<IAdvertModerationService>();
        moderation.Setup(m => m.EnsureImageAllowedAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        moderation.Setup(m => m.EnsureDocumentAllowedAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _service = new UploadService(storage, moderation.Object, Mock.Of<ILogger<UploadService>>());
    }

    [Fact]
    public async Task SaveImagesAsync_StoresFilesAndReturnsUrls()
    {
        var files = new FormFileCollection
        {
            CreateFormFile("photo.jpg", "image/jpeg", [0xFF, 0xD8, 0xFF, 0x00]),
        };

        var urls = await _service.SaveImagesAsync(_userId, files);

        Assert.Single(urls);
        Assert.StartsWith($"/uploads/images/{_userId:N}/", urls[0]);
        Assert.EndsWith(".jpg", urls[0]);

        var physicalPath = Path.Combine(_root, "wwwroot", urls[0].TrimStart('/'));
        Assert.True(File.Exists(physicalPath));
    }

    [Fact]
    public async Task SaveResumeAsync_AcceptsPdf()
    {
        var file = CreateFormFile("cv.pdf", "application/pdf", "%PDF-1.4"u8.ToArray());

        var url = await _service.SaveResumeAsync(_userId, file);

        Assert.StartsWith($"/uploads/resumes/{_userId:N}/", url);
        Assert.EndsWith(".pdf", url);
    }

    [Fact]
    public async Task SaveImagesAsync_UnsupportedType_Throws()
    {
        var files = new FormFileCollection
        {
            CreateFormFile("notes.txt", "text/plain", "hello"u8.ToArray()),
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.SaveImagesAsync(_userId, files));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static FormFile CreateFormFile(string fileName, string contentType, byte[] content)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }
}
