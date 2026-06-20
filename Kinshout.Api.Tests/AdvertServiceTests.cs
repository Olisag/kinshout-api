using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Kinshout.Api.Tests;

public class AdvertServiceTests
{
    private static AdvertService CreateService(
        KinshoutDbContext db,
        AiAdvertAnalysis? analysis = null,
        Mock<IAdvertModerationService>? moderation = null,
        IWebHostEnvironment? environment = null)
    {
        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.AnalyzeAdvertAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Category>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(analysis ?? TestDbFactory.SampleAnalysis());

        var moderationMock = moderation ?? CreatePassThroughModeration();
        var env = environment ?? CreateWebEnvironment();

        return new AdvertService(db, openAi.Object, moderationMock.Object, env);
    }

    private static Mock<IAdvertModerationService> CreatePassThroughModeration()
    {
        var mock = new Mock<IAdvertModerationService>();
        mock.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(m => m.EnsureImageAllowedAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private static IWebHostEnvironment CreateWebEnvironment(string? root = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "kinshout-advert-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));

        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.WebRootPath).Returns(Path.Combine(root, "wwwroot"));
        env.Setup(e => e.ContentRootPath).Returns(root);
        return env.Object;
    }

    [Fact]
    public async Task CreateAsync_EmptyText_Throws()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(user.Id, new CreateAdvertRequestDto("  ", null, null, null, null, null)));
    }

    [Fact]
    public async Task CreateAsync_MissingWhatsApp_Throws()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db, withWhatsApp: false);
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(
                user.Id,
                new CreateAdvertRequestDto("Je cherche un appartement", null, null, null, null, null)));

        Assert.Contains("WhatsApp", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_BlockedText_Throws()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var moderation = CreatePassThroughModeration();
        moderation.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AdvertModerationException("Contenu interdit."));
        var service = CreateService(db, moderation: moderation);

        await Assert.ThrowsAsync<AdvertModerationException>(() =>
            service.CreateAsync(
                user.Id,
                new CreateAdvertRequestDto("Texte interdit", null, null, null, null, null)));
    }

    [Fact]
    public async Task CreateAsync_ExternalImageUrl_Throws()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(
                user.Id,
                new CreateAdvertRequestDto(
                    "Appartement à louer",
                    null,
                    null,
                    ["https://images.unsplash.com/photo.jpg"],
                    null,
                    null)));

        Assert.Contains("Internet", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_ValidRequest_PersistsImagesAndResume()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var root = Path.Combine(Path.GetTempPath(), "kinshout-advert-tests", Guid.NewGuid().ToString("N"));
        var webRoot = Path.Combine(root, "wwwroot");
        var imagePath = Path.Combine(webRoot, "uploads", "images", user.Id.ToString("N"));
        Directory.CreateDirectory(imagePath);
        await File.WriteAllBytesAsync(Path.Combine(imagePath, "a.jpg"), [0xFF, 0xD8, 0xFF, 0x00]);
        await File.WriteAllBytesAsync(Path.Combine(imagePath, "b.jpg"), [0xFF, 0xD8, 0xFF, 0x00]);

        var service = CreateService(db, environment: CreateWebEnvironment(root));
        var created = await service.CreateAsync(
            user.Id,
            new CreateAdvertRequestDto(
                "Je cherche un appartement 2 chambres à Gombe",
                "500 $",
                "Gombe",
                [$"/uploads/images/{user.Id:N}/a.jpg", $"/uploads/images/{user.Id:N}/b.jpg"],
                "/uploads/cv.pdf",
                "demande"));

        Assert.Equal("Appartement à Gombe", created.Title);
        Assert.Equal(2, created.ImageUrls.Count);
        Assert.Equal("/uploads/cv.pdf", created.ResumeUrl);
        Assert.Equal("+243900000001", created.WhatsAppNumber);
        Assert.Equal("demande", created.Intent);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ListAsync_FiltersByCategory()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var otherCategory = new Category
        {
            Slug = "electronique",
            Label = "Électronique",
            Icon = "▯",
            IsSystem = true,
        };
        db.Categories.Add(otherCategory);
        await db.SaveChangesAsync();

        db.Adverts.AddRange(
            new Advert
            {
                UserId = user.Id,
                CategoryId = category.Id,
                Title = "Annonce immo",
                Description = "Desc",
                Category = category,
                User = user,
            },
            new Advert
            {
                UserId = user.Id,
                CategoryId = otherCategory.Id,
                Title = "Annonce tech",
                Description = "Desc",
                Category = otherCategory,
                User = user,
            });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var results = await service.ListAsync(category.Id);

        Assert.Single(results);
        Assert.Equal("Annonce immo", results[0].Title);
    }
}
