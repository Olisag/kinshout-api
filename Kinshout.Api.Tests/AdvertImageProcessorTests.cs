using Kinshout.Api.Models;
using Kinshout.Api.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Kinshout.Api.Tests;

public class AdvertImageProcessorTests
{
    [Fact]
    public async Task CreateDisplayImageAsync_ResizesLargeImage()
    {
        await using var source = new MemoryStream();
        using (var image = new Image<Rgba32>(2400, 1600))
        {
            await image.SaveAsJpegAsync(source);
        }

        source.Position = 0;
        var processor = new AdvertImageProcessor(Microsoft.Extensions.Logging.Abstractions.NullLogger<AdvertImageProcessor>.Instance);
        await using var display = await processor.CreateDisplayImageAsync(source);

        Assert.NotNull(display);
        using var decoded = await Image.LoadAsync(display);
        Assert.True(decoded.Width <= AdvertImageUrls.DisplayMaxWidth);
    }
}
