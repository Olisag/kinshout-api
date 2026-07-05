using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Tests;

public class SavedAdvertServiceTests
{
    [Fact]
    public async Task SaveAndListSavedAsync()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var otherUser = new User
        {
            Email = "other@kinshout.test",
            DisplayName = "Other",
            WhatsAppNumber = "+243900000002",
        };
        db.Users.Add(otherUser);

        var advert = new Advert
        {
            UserId = otherUser.Id,
            CategoryId = category.Id,
            Title = "Appartement Gombe",
            Description = "Bel appartement",
            Category = category,
            User = otherUser,
            IsPublished = true,
        };
        db.Adverts.Add(advert);
        await db.SaveChangesAsync();

        var service = new SavedAdvertService(db, TestDbFactory.CreateAdvertDtoMapper());
        var savedDto = await service.SaveAsync(user.Id, advert.Id);
        var saved = await service.ListSavedAsync(user.Id);

        Assert.Equal(1, saved.TotalCount);
        Assert.Equal(advert.Id, saved.Items[0].Id);
        Assert.Equal(1, saved.Items[0].LikeCount);
        Assert.True(saved.Items[0].IsSaved);
        Assert.True(savedDto.IsSaved);
        Assert.Equal(1, savedDto.LikeCount);
        Assert.Contains(advert.Id, await service.ListSavedIdsAsync(user.Id));

        var updated = await db.Adverts.AsNoTracking().FirstAsync(a => a.Id == advert.Id);
        Assert.Equal(1, updated.LikeCount);
    }

    [Fact]
    public async Task UnsaveAsync_RemovesAdvert()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var advert = new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Studio",
            Description = "Desc",
            Category = category,
            User = user,
            IsPublished = true,
        };
        db.Adverts.Add(advert);
        await db.SaveChangesAsync();

        var service = new SavedAdvertService(db, TestDbFactory.CreateAdvertDtoMapper());
        await service.SaveAsync(user.Id, advert.Id);
        var unsavedDto = await service.UnsaveAsync(user.Id, advert.Id);

        var saved = await service.ListSavedAsync(user.Id);
        Assert.Empty(saved.Items);
        Assert.Empty(await service.ListSavedIdsAsync(user.Id));
        Assert.False(unsavedDto.IsSaved);
        Assert.Equal(0, unsavedDto.LikeCount);

        var updated = await db.Adverts.AsNoTracking().FirstAsync(a => a.Id == advert.Id);
        Assert.Equal(0, updated.LikeCount);
    }
}
