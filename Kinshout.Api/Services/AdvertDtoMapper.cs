using System.Text.Json;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

public interface IAdvertDtoMapper
{
    AdvertDto ToDto(Advert advert, bool isSaved = false, bool includeDisplayUrls = false);
    List<AdvertDto> ToDtos(IReadOnlyList<Advert> adverts, IReadOnlySet<Guid> savedIds, bool includeDisplayUrls = false);
}

public sealed class AdvertDtoMapper(IUploadUrlResolver uploadUrls) : IAdvertDtoMapper
{
    public AdvertDto ToDto(Advert advert, bool isSaved = false, bool includeDisplayUrls = false)
    {
        var tags = JsonSerializer.Deserialize<List<string>>(advert.TagsJson ?? "[]") ?? [];
        var storedUrls = JsonSerializer.Deserialize<List<string>>(advert.ImageUrlsJson ?? "[]") ?? [];
        var publicUrls = AdvertImageUrls.ToPublicUrls(uploadUrls, storedUrls);
        var thumbnailUrls = AdvertImageUrls.ToPublicUrls(uploadUrls, AdvertImageUrls.BuildListingUrls(storedUrls));
        var displayUrls = includeDisplayUrls
            ? AdvertImageUrls.ToPublicUrls(uploadUrls, AdvertImageUrls.BuildDisplayUrls(storedUrls))
            : [];

        var contact = AdvertSourceMapper.ToContactDto(advert);
        var sortDate = AdvertSourceMapper.SortDate(advert);
        return new AdvertDto(
            advert.Id,
            advert.Title,
            advert.Description,
            advert.Price,
            advert.Location,
            advert.Intent.ToString().ToLowerInvariant(),
            advert.Category.Slug,
            advert.Category.Label,
            advert.Category.Icon,
            publicUrls,
            thumbnailUrls,
            displayUrls,
            uploadUrls.ToPublicUrl(advert.ResumeUrl),
            contact?.WhatsApp ?? advert.User?.WhatsAppNumber,
            tags,
            TimeHelpers.FormatRelative(sortDate),
            advert.AiConfidence,
            advert.AiSummary,
            advert.ViewCount,
            advert.LikeCount,
            isSaved,
            advert.IsExternal,
            AdvertSourceMapper.ToSourceDto(advert),
            AdvertSourceMapper.ToDetailsDto(advert),
            contact);
    }

    public List<AdvertDto> ToDtos(IReadOnlyList<Advert> adverts, IReadOnlySet<Guid> savedIds, bool includeDisplayUrls = false) =>
        adverts.Select(a => ToDto(a, savedIds.Contains(a.Id), includeDisplayUrls)).ToList();
}
