using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class AdvertImageUrlsTests
{
    [Fact]
    public void GetThumbnailPath_ForKinshoutImage_UsesWebpSuffix()
    {
        var original = "/uploads/images/abc123/def4567890abcdef.jpg";
        Assert.Equal("/uploads/images/abc123/def4567890abcdef_thumb.webp", AdvertImageUrls.GetThumbnailPath(original));
    }

    [Fact]
    public void GetDisplayPath_ForKinshoutImage_UsesWebpSuffix()
    {
        var original = "/uploads/images/abc123/def4567890abcdef.jpg";
        Assert.Equal("/uploads/images/abc123/def4567890abcdef_display.webp", AdvertImageUrls.GetDisplayPath(original));
    }

    [Fact]
    public void GetThumbnailPath_ForExternalUrl_ReturnsNull()
    {
        Assert.Null(AdvertImageUrls.GetThumbnailPath("https://cdn.example/photo.jpg"));
    }

    [Fact]
    public void BuildListingUrls_FallsBackToOriginalForExternalImages()
    {
        var urls = AdvertImageUrls.BuildListingUrls([
            "/uploads/images/abc123/def4567890abcdef.png",
            "https://cdn.example/photo.jpg",
        ]);

        Assert.Equal("/uploads/images/abc123/def4567890abcdef_thumb.webp", urls[0]);
        Assert.Equal("https://cdn.example/photo.jpg", urls[1]);
    }

    [Fact]
    public void BuildDisplayUrls_FallsBackToOriginalForExternalImages()
    {
        var urls = AdvertImageUrls.BuildDisplayUrls([
            "/uploads/images/abc123/def4567890abcdef.png",
            "https://cdn.example/photo.jpg",
        ]);

        Assert.Equal("/uploads/images/abc123/def4567890abcdef_display.webp", urls[0]);
        Assert.Equal("https://cdn.example/photo.jpg", urls[1]);
    }
}
