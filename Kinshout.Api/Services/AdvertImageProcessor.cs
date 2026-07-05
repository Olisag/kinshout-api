using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Kinshout.Api.Services;

public interface IAdvertImageProcessor
{
    Task<MemoryStream?> CreateListingThumbnailAsync(Stream source, CancellationToken ct = default);
    Task<MemoryStream?> CreateDisplayImageAsync(Stream source, CancellationToken ct = default);
}

public sealed class AdvertImageProcessor(ILogger<AdvertImageProcessor> logger) : IAdvertImageProcessor
{
    public Task<MemoryStream?> CreateListingThumbnailAsync(Stream source, CancellationToken ct = default) =>
        CreateWebpVariantAsync(source, AdvertImageUrls.ThumbnailMaxWidth, quality: 80, ct);

    public Task<MemoryStream?> CreateDisplayImageAsync(Stream source, CancellationToken ct = default) =>
        CreateWebpVariantAsync(source, AdvertImageUrls.DisplayMaxWidth, quality: 85, ct);

    private async Task<MemoryStream?> CreateWebpVariantAsync(
        Stream source,
        int maxWidth,
        int quality,
        CancellationToken ct)
    {
        try
        {
            using var image = await Image.LoadAsync(source, ct);
            if (image.Width > maxWidth)
            {
                image.Mutate(x => x
                    .AutoOrient()
                    .Resize(new ResizeOptions
                    {
                        Size = new Size(maxWidth, 0),
                        Mode = ResizeMode.Max,
                    }));
            }
            else
            {
                image.Mutate(x => x.AutoOrient());
            }

            var output = new MemoryStream();
            await image.SaveAsWebpAsync(output, new WebpEncoder { Quality = quality }, ct);
            output.Position = 0;
            return output;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Advert image variant generation failed (maxWidth={MaxWidth}).", maxWidth);
            return null;
        }
    }
}
