using Kinshout.Api.Controllers;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Kinshout.Api.Tests;

public class VideosControllerStreamTests
{
    [Fact]
    public async Task Stream_RedirectsToDirectStorageUri_WhenAvailable()
    {
        var id = Guid.NewGuid();
        var directUri = new Uri("https://storage.example/videos/clip.mp4?sig=abc");
        var videos = new Mock<IVideoService>();
        videos.Setup(v => v.GetDirectStreamUriAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(directUri);
        var controller = CreateController(videos.Object);

        var result = await controller.Stream(id, CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal(directUri.AbsoluteUri, redirect.Url);
        Assert.False(redirect.Permanent);
        Assert.Equal("public,max-age=3600", controller.Response.Headers.CacheControl.ToString());
        videos.Verify(v => v.OpenStreamAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Stream_StreamsWithRangeSupport_WhenNoDirectUri()
    {
        var id = Guid.NewGuid();
        var videos = new Mock<IVideoService>();
        videos.Setup(v => v.GetDirectStreamUriAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Uri?)null);
        videos.Setup(v => v.OpenStreamAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadFileContent(new MemoryStream([1, 2, 3]), "video/mp4"));
        var controller = CreateController(videos.Object);

        var result = await controller.Stream(id, CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.True(file.EnableRangeProcessing);
        Assert.Equal("video/mp4", file.ContentType);
    }

    [Fact]
    public async Task Stream_ReturnsNotFound_WhenVideoIsMissing()
    {
        var controller = CreateController(Mock.Of<IVideoService>());

        var result = await controller.Stream(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    private static VideosController CreateController(IVideoService videos) =>
        new(videos)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
}
