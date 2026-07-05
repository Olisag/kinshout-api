using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Kinshout.Api.Tests;

public class AdvertImageVariantBackfillTests
{
    [Fact]
    public async Task EnsureVariantAsync_CreatesMissingThumbnailForNativeUpload()
    {
        var (storage, userId, originalPath) = await SeedNativeImageAsync();
        var processor = new AdvertImageProcessor(Mock.Of<ILogger<AdvertImageProcessor>>());

        var thumbPath = AdvertImageUrls.GetThumbnailPath(originalPath)!;
        Assert.False(await storage.ExistsAsync(thumbPath));

        var created = await AdvertImageVariantBackfillScheduler.EnsureVariantAsync(
            storage,
            processor,
            originalPath,
            AdvertImageUrls.ThumbnailSuffix);

        Assert.True(created);
        Assert.True(await storage.ExistsAsync(thumbPath));
    }

    [Fact]
    public async Task EnsureVariantAsync_SkipsWhenThumbnailAlreadyExists()
    {
        var (storage, _, originalPath) = await SeedNativeImageAsync();
        var processor = new AdvertImageProcessor(Mock.Of<ILogger<AdvertImageProcessor>>());

        await AdvertImageVariantBackfillScheduler.EnsureVariantAsync(
            storage,
            processor,
            originalPath,
            AdvertImageUrls.ThumbnailSuffix);

        var createdAgain = await AdvertImageVariantBackfillScheduler.EnsureVariantAsync(
            storage,
            processor,
            originalPath,
            AdvertImageUrls.ThumbnailSuffix);

        Assert.False(createdAgain);
    }

    [Fact]
    public async Task ScheduleBackfill_ProcessesNativeAdvertsOnly()
    {
        var (storage, userId, originalPath) = await SeedNativeImageAsync();
        await using var db = TestDbFactory.Create();
        var (owner, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        db.Adverts.Add(new Advert
        {
            UserId = owner.Id,
            CategoryId = category.Id,
            Title = "Native listing",
            Description = "Has native upload",
            IsPublished = true,
            ImageUrlsJson = $"[\"{originalPath}\"]",
        });
        db.Adverts.Add(new Advert
        {
            UserId = owner.Id,
            CategoryId = category.Id,
            Title = "External listing",
            Description = "Hotlinked",
            IsPublished = true,
            ImageUrlsJson = "[\"https://cdn.example.com/photo.jpg\"]",
            SourceProvider = AdvertSourceProvider.FacebookMarketplace,
            SourceExternalId = "ext-1",
            SourceExternalUrl = "https://facebook.com/item/ext-1",
            SourceImportedAt = DateTime.UtcNow,
            SourceLastSeenAt = DateTime.UtcNow,
            SourceFirstSeenAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var scopeFactory = CreateScopeFactory(db, storage);
        var scheduler = new AdvertImageVariantBackfillScheduler(
            scopeFactory,
            Mock.Of<ILogger<AdvertImageVariantBackfillScheduler>>());

        scheduler.ScheduleBackfill();
        await WaitForBackfillAsync();

        var thumbPath = AdvertImageUrls.GetThumbnailPath(originalPath)!;
        Assert.True(await storage.ExistsAsync(thumbPath));
    }

    private static async Task<(LocalUploadStorage Storage, Guid UserId, string OriginalPath)> SeedNativeImageAsync()
    {
        var userId = Guid.Parse("cccccccccccccccccccccccccccccccc");
        var root = Path.Combine(Path.GetTempPath(), "kinshout-thumb-backfill", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.WebRootPath).Returns(Path.Combine(root, "wwwroot"));
        env.Setup(e => e.ContentRootPath).Returns(root);

        var storage = new LocalUploadStorage(env.Object, Mock.Of<ILogger<LocalUploadStorage>>());
        var jpeg = MinimalJpeg();
        await using var stream = new MemoryStream(jpeg);
        var originalPath = await storage.SaveNamedAsync("images", userId, stream, "listing.jpg");
        return (storage, userId, originalPath);
    }

    private static IServiceScopeFactory CreateScopeFactory(KinshoutDbContext db, IUploadStorage storage)
    {
        var processor = new AdvertImageProcessor(Mock.Of<ILogger<AdvertImageProcessor>>());
        var scope = new Mock<IServiceScope>();
        var provider = new Mock<IServiceProvider>();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        provider.Setup(p => p.GetService(typeof(KinshoutDbContext))).Returns(db);
        provider.Setup(p => p.GetService(typeof(IUploadStorage))).Returns(storage);
        provider.Setup(p => p.GetService(typeof(IAdvertImageProcessor))).Returns(processor);

        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope.Object);
        return factory.Object;
    }

    private static async Task WaitForBackfillAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(50);
        }
    }

    private static byte[] MinimalJpeg() =>
        Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDAREAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAb/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAAAAX/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAGfAP/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAQUCf//EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQMBAT8Bf//EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQIBAT8Bf//EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEABj8Cf//EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAT8hf//Z");
}
