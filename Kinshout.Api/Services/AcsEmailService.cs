using Azure;
using Azure.Communication.Email;
using Azure.Identity;
using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

/// <summary>Sends through Azure Communication Services, signed in with the app's managed identity.</summary>
public class AcsEmailService(IOptions<EmailSettings> options, ILogger<AcsEmailService> logger) : IEmailService
{
    private readonly EmailSettings _settings = options.Value;
    private readonly Lazy<EmailClient> _client = new(() =>
        new EmailClient(new Uri(options.Value.AcsEndpoint), new DefaultAzureCredential()));

    public async Task SendAsync(string toAddress, string subject, string plainTextBody, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toAddress))
            return;

        var message = new EmailMessage(
            _settings.FromAddress,
            toAddress.Trim(),
            new EmailContent(subject) { PlainText = plainTextBody });

        var operation = await _client.Value.SendAsync(WaitUntil.Started, message, ct);
        logger.LogInformation(
            "Queued email to {To}: {Subject} (operation {OperationId})",
            toAddress,
            subject,
            operation.Id);
    }
}
