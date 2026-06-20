using Kinshout.Api.Configuration;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public interface IWhatsAppAuthService
{
    Task<WhatsAppCodeResponseDto> SendCodeAsync(string whatsAppNumber, CancellationToken ct = default);
    Task<AuthResponseDto> VerifyAndSignInAsync(
        WhatsAppVerifyRequestDto request,
        string clientId,
        CancellationToken ct = default);
}

public class WhatsAppAuthService(
    IMemoryCache cache,
    IOptions<WhatsAppAuthSettings> settings,
    IHostEnvironment environment,
    IAuthService auth,
    ILogger<WhatsAppAuthService> logger) : IWhatsAppAuthService
{
    private readonly WhatsAppAuthSettings _settings = settings.Value;

    public Task<WhatsAppCodeResponseDto> SendCodeAsync(string whatsAppNumber, CancellationToken ct = default)
    {
        var phone = WhatsAppHelper.Normalize(whatsAppNumber);
        var code = GenerateCode();
        var expiresAt = DateTime.UtcNow.AddMinutes(_settings.CodeExpirationMinutes);

        cache.Set(CacheKey(phone), code, expiresAt);
        logger.LogInformation("WhatsApp sign-in code generated for {Phone}", phone);

        var exposeCode = (_settings.ExposeCodeInDevelopment && environment.IsDevelopment())
            || _settings.ExposeCodeUntilDeliveryEnabled;
        return Task.FromResult(new WhatsAppCodeResponseDto(
            "Code de connexion envoyé sur WhatsApp.",
            expiresAt,
            exposeCode ? code : null));
    }

    public async Task<AuthResponseDto> VerifyAndSignInAsync(
        WhatsAppVerifyRequestDto request,
        string clientId,
        CancellationToken ct = default)
    {
        var phone = WhatsAppHelper.Normalize(request.WhatsAppNumber);
        var submitted = request.Code?.Trim() ?? string.Empty;

        if (submitted.Length != _settings.CodeLength || !submitted.All(char.IsDigit))
            throw new UnauthorizedAccessException("Code invalide.");

        if (!cache.TryGetValue(CacheKey(phone), out string? expected)
            || !string.Equals(expected, submitted, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Code incorrect ou expiré.");
        }

        cache.Remove(CacheKey(phone));
        return await auth.SignInWithWhatsAppAsync(phone, request.DisplayName, clientId, ct);
    }

    private string GenerateCode()
    {
        var max = (int)Math.Pow(10, _settings.CodeLength);
        return Random.Shared.Next(0, max).ToString($"D{_settings.CodeLength}");
    }

    private static string CacheKey(string phone) => $"whatsapp-otp:{phone}";
}
