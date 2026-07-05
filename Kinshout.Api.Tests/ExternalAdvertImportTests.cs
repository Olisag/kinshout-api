using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Kinshout.Api.Tests;

public class ExternalAdvertImportTests
{
    [Fact]
    public async Task ImportAsync_CreatesAndUpdatesExternalAdvert()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateImportService(db);

        var dto = SampleImport("fb-123", "Premier titre");
        var first = await service.ImportAsync([dto]);
        Assert.Equal(1, first.Created);
        Assert.Equal(0, first.Updated);

        var advert = await db.Adverts.SingleAsync();
        Assert.Equal(AdvertSourceProvider.FacebookMarketplace, advert.SourceProvider);
        Assert.Equal("fb-123", advert.SourceExternalId);
        Assert.True(advert.IsExternal);
        Assert.Equal("Premier titre", advert.Title);

        var updatedDto = dto with { Title = "Titre mis à jour" };
        var second = await service.ImportAsync([updatedDto]);
        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.Updated);
        Assert.Equal("Titre mis à jour", (await db.Adverts.SingleAsync()).Title);
    }

    [Fact]
    public async Task ImportAsync_UnchangedWhenContentMatches()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateImportService(db);
        var dto = SampleImport("mc-1", "Stable");

        await service.ImportAsync([dto]);
        var second = await service.ImportAsync([dto with
        {
            Source = dto.Source with { LastSeenAt = DateTime.UtcNow },
        }]);

        Assert.Equal(1, second.Unchanged);
    }

    [Fact]
    public async Task ImportAsync_DeletesRemovedAdvertFromDatabase()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateImportService(db);
        var dto = SampleImport("rm-1", "À retirer");

        await service.ImportAsync([dto]);
        var removed = await service.ImportAsync([dto with { Status = "removed" }]);

        Assert.Equal(1, removed.Updated);
        Assert.Empty(await db.Adverts.ToListAsync());
    }

    [Fact]
    public async Task ImportAsync_DeletesInactiveAdvertFromDatabase()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateImportService(db);
        var dto = SampleImport("in-1", "Inactif");

        await service.ImportAsync([dto]);
        var inactive = await service.ImportAsync([dto with { Status = "inactive" }]);

        Assert.Equal(1, inactive.Updated);
        Assert.Empty(await db.Adverts.ToListAsync());
    }

    [Fact]
    public async Task ListAsync_FiltersBySource()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        db.Adverts.Add(new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Native",
            Description = "Kinshout",
            IsPublished = true,
        });
        db.Adverts.Add(new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Facebook",
            Description = "External",
            IsPublished = true,
            SourceProvider = AdvertSourceProvider.FacebookMarketplace,
            SourceProviderName = "Facebook Marketplace",
            SourceExternalId = "x1",
            SourceExternalUrl = "https://facebook.com/marketplace/item/x1",
            SourceImportedAt = DateTime.UtcNow,
            SourceLastSeenAt = DateTime.UtcNow,
            SourceFirstSeenAt = DateTime.UtcNow,
            DetailsJson = "{}",
            ContactJson = "{}",
        });
        await db.SaveChangesAsync();

        var service = CreateAdvertService(db);
        var kinshout = await service.ListAsync(source: "kinshout");
        var facebook = await service.ListAsync(source: "facebook_marketplace");
        var external = await service.ListAsync(source: "external");

        Assert.Single(kinshout.Items);
        Assert.Equal("Native", kinshout.Items[0].Title);
        Assert.False(kinshout.Items[0].IsExternal);

        Assert.Single(facebook.Items);
        Assert.True(facebook.Items[0].IsExternal);
        Assert.Equal("Facebook Marketplace", facebook.Items[0].Source!.ProviderName);

        Assert.Single(external.Items);
        Assert.Equal("Facebook", external.Items[0].Title);
    }

    [Fact]
    public async Task GetKnownAdvertKeysAsync_ReturnsExternalIds()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateImportService(db);

        await service.ImportAsync([SampleImport("mc-99", "Test")]);
        var keys = await service.GetKnownAdvertKeysAsync();

        Assert.Single(keys);
        Assert.Equal(AdvertSourceProvider.FacebookMarketplace, keys[0].Provider);
        Assert.Equal("mc-99", keys[0].ExternalId);
    }

    private static ExternalAdvertImportService CreateImportService(
        KinshoutDbContext db,
        IExternalAdvertImageMirrorService? mirror = null) =>
        new(db, mirror ?? CreatePassthroughMirror());

    private static IExternalAdvertImageMirrorService CreatePassthroughMirror()
    {
        var mock = new Mock<IExternalAdvertImageMirrorService>();
        mock.Setup(m => m.MirrorAsync(
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                IReadOnlyList<string>? urls,
                string _,
                string __,
                Guid ___,
                CancellationToken ____) =>
                urls?.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).Take(10).ToList()
                ?? []);
        mock.Setup(m => m.DeleteMirroredAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock.Object;
    }

    private static ImportExternalAdvertDto SampleImport(string externalId, string title) =>
        new(
            new ImportExternalAdvertSourceDto(
                AdvertSourceProvider.FacebookMarketplace,
                "Facebook Marketplace",
                externalId,
                $"https://www.facebook.com/marketplace/item/{externalId}/",
                DateTime.UtcNow,
                DateTime.UtcNow,
                DateTime.UtcNow),
            "immobilier",
            "appartement_a_louer",
            title,
            new ImportExternalAdvertPriceDto(2200, "USD", "$2 200 / mois", "monthly", true),
            new ImportExternalAdvertLocationDto("Kinshasa", "Gombe", null, null, "Gombe, Kinshasa"),
            new ImportExternalAdvertDetailsDto(2, 2, 95, true, 5, "apartment", "good", true, false, null),
            "Description test",
            ["https://example.com/1.jpg"],
            new ImportExternalAdvertContactDto("John K.", null, "+243812345678", "+243812345678", "whatsapp", true),
            PublishedAt: DateTime.UtcNow.AddHours(-2),
            Modality: "rent",
            Ai: new ImportExternalAdvertAiDto(["meublé"], "Résumé", ["location"]));

    private static AdvertService CreateAdvertService(KinshoutDbContext db)
    {
        var moderation = new Mock<IAdvertModerationService>();
        moderation.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        moderation.Setup(m => m.EnsureImageAllowedAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var root = Path.Combine(Path.GetTempPath(), "kinshout-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.WebRootPath).Returns(Path.Combine(root, "wwwroot"));
        env.Setup(e => e.ContentRootPath).Returns(root);
        var storage = new LocalUploadStorage(env.Object, Mock.Of<ILogger<LocalUploadStorage>>());

        return new AdvertService(db, Mock.Of<IOpenAiService>(), moderation.Object, storage, TestDbFactory.CreateAdvertDtoMapper());
    }
}
