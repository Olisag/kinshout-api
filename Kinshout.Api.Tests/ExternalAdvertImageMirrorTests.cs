using System.Net;
using Kinshout.Api.Configuration;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public class ExternalAdvertImageMirrorTests
{
    [Fact]
    public async Task MirrorAsync_SkipsKinshoutPaths()
    {
        var (service, _) = CreateService(new FakeHttpHandler());
        var kinshoutPath = "/uploads/images/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/photo.jpg";

        var result = await service.MirrorAsync(
            [kinshoutPath, "https://example.com/other.jpg"],
            "facebook_marketplace",
            "fb-1",
            Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        Assert.Single(result);
        Assert.Equal(kinshoutPath, result[0]);
    }

    [Fact]
    public async Task MirrorAsync_DownloadsAndStoresImage()
    {
        var jpeg = MinimalJpeg();
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(jpeg)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") },
            },
        });

        var (service, storage) = CreateService(handler);
        var ownerId = Guid.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        var result = await service.MirrorAsync(
            ["https://cdn.example.com/listing.jpg"],
            "facebook_marketplace",
            "item-42",
            ownerId);

        Assert.Single(result);
        Assert.StartsWith($"/uploads/images/{ownerId:N}/", result[0]);
        Assert.Contains("facebookmarketplace_item42_0", result[0]);
        Assert.True(await storage.ExistsAsync(result[0]));
        var thumbPath = AdvertImageUrls.GetThumbnailPath(result[0]);
        Assert.NotNull(thumbPath);
        Assert.True(await storage.ExistsAsync(thumbPath));
    }

    private static (ExternalAdvertImageMirrorService Service, LocalUploadStorage Storage) CreateService(HttpMessageHandler handler)
    {
        var root = Path.Combine(Path.GetTempPath(), "kinshout-mirror-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.WebRootPath).Returns(Path.Combine(root, "wwwroot"));
        env.Setup(e => e.ContentRootPath).Returns(root);

        var storage = new LocalUploadStorage(env.Object, Mock.Of<ILogger<LocalUploadStorage>>());
        var factory = new FakeHttpClientFactory(handler);
        var importOptions = Options.Create(new ImportSettings { MirrorExternalImages = true });

        var service = new ExternalAdvertImageMirrorService(
            factory,
            storage,
            new AdvertImageProcessor(Mock.Of<ILogger<AdvertImageProcessor>>()),
            importOptions,
            Mock.Of<ILogger<ExternalAdvertImageMirrorService>>());

        return (service, storage);
    }

    private static byte[] MinimalJpeg()
    {
        // 1x1 red pixel JPEG
        return Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDAREAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAb/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAAAAX/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAGfAP/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAQUCf//EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQMBAT8Bf//EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQIBAT8Bf//EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEABj8Cf//EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAT8hf//Z");
    }

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
