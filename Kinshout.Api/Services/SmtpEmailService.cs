using System.Net;
using System.Net.Mail;
using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public class SmtpEmailService(
    IOptions<EmailSettings> options,
    ILogger<SmtpEmailService> logger) : IEmailService
{
    private readonly EmailSettings _settings = options.Value;

    public async Task SendAsync(string toAddress, string subject, string plainTextBody, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toAddress))
            return;

        if (!_settings.Enabled)
        {
            logger.LogInformation(
                "Email disabled — would send to {To}: {Subject}",
                toAddress,
                subject);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.SmtpHost))
            throw new InvalidOperationException("Email:SmtpHost must be configured when Email:Enabled is true.");

        using var message = new MailMessage
        {
            From = new MailAddress(_settings.FromAddress, _settings.FromName),
            Subject = subject,
            Body = plainTextBody,
            IsBodyHtml = false,
        };
        message.To.Add(toAddress.Trim());

        using var client = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
        {
            EnableSsl = _settings.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        if (!string.IsNullOrWhiteSpace(_settings.SmtpUsername))
            client.Credentials = new NetworkCredential(_settings.SmtpUsername, _settings.SmtpPassword);

        await client.SendMailAsync(message, ct);
        logger.LogInformation("Sent email to {To}: {Subject}", toAddress, subject);
    }
}
