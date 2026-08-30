namespace Kinshout.Api.Services;

public interface IEmailService
{
    Task SendAsync(string toAddress, string subject, string plainTextBody, CancellationToken ct = default);
}
