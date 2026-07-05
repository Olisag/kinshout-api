using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Kinshout.Api.Services;

public interface IAdvertImageProcessor
{
    Task<MemoryStream?> CreateListingThumbnailAsync(Stream source, CancellationToken ct = default);
}

public sealed class AdvertImageProcessor(ILogger<AdvertImageProcessor> logger) : IAdvertImageProcessor
{
    public async Task<MemoryStream?> CreateListingThumbnailAsync(Stream source, CancellationToken ct = default)
    {
        try
        {
            using var image = await Image.LoadAsync(source, ct);
            if (image.Width <= AdvertImageUrls.ThumbnailMaxWidth)
            {
                image.Mutate(x => x.AutoOrient());
            }
            else
            {
                image.Mutate(x => x
                    .AutoOrient()
                    .Resize(new ResizeOptions
                    {
                        Size = new Size(AdvertImageUrls.ThumbnailMaxWidth, 0),
                        Mode = ResizeMode.Max,
                    }));
            }

            var output = new MemoryStream();
            await image.SaveAsWebpAsync(
                output,
                new WebpEncoder { Quality = 80 },
                ct);
            output.Position = 0;
            return output;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Listing thumbnail generation failed.");
            return null;
        }
    }
}
