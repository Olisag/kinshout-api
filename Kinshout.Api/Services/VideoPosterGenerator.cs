using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Kinshout.Api.Services;

/// <summary>Cheap poster fallback when no client poster is available (legacy videos).</summary>
public static class VideoPosterGenerator
{
    public const int Width = 640;
    public const int Height = 360;

    public static async Task<MemoryStream> CreatePlaceholderAsync(CancellationToken ct = default)
    {
        using var image = new Image<Rgba32>(Width, Height);
        image.Mutate(ctx => ctx.BackgroundColor(new Rgba32(28, 28, 32)));

        var output = new MemoryStream();
        await image.SaveAsWebpAsync(output, new WebpEncoder { Quality = 70 }, ct);
        output.Position = 0;
        return output;
    }
}
