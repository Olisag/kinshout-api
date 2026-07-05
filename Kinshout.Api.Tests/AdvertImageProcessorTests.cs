using Kinshout.Api.Models;
using Kinshout.Api.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Kinshout.Api.Tests;

public class AdvertImageProcessorTests
{
    [Fact]
    public async Task CreateListingThumbnailAsync_ResizesLargeImage()
    {
        await using var source = new MemoryStream();
        using (var image = new Image<Rgba32>(1200, 800))
        {
            await image.SaveAsJpegAsync(source);
        }

        source.Position = 0;
        var processor = new AdvertImageProcessor(Microsoft.Extensions.Logging.Abstractions.NullLogger<AdvertImageProcessor>.Instance);
        await using var thumb = await processor.CreateListingThumbnailAsync(source);

        Assert.NotNull(thumb);
        using var decoded = await Image.LoadAsync(thumb);
        Assert.True(decoded.Width <= AdvertImageUrls.ThumbnailMaxWidth);
    }
}
