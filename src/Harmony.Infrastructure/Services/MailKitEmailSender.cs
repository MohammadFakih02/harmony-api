using Harmony.Application.Interfaces.Services;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// SMTP implementation of <see cref="IEmailSender"/> — the ONLY file touching MailKit (same
/// SDK-containment pattern as WebPushSender over Lib.Net.Http.WebPush). Built from the
/// <c>Smtp</c> config section. Dev/test point this at Mailpit (no auth, no TLS); prod would
/// point it at SES SMTP or similar with credentials + StartTls. Every caller sends best-effort
/// (never lets a failed send abort the calling request) except where a caller explicitly awaits
/// it inline for a test assertion — the sender itself just logs and rethrows so the caller decides.
/// </summary>
public sealed class MailKitEmailSender : IEmailSender
{
    private readonly string _host;
    private readonly int _port;
    private readonly bool _useSsl;
    private readonly string _username;
    private readonly string _password;
    private readonly string _from;
    private readonly string _fromName;
    private readonly ILogger<MailKitEmailSender> _logger;

    public MailKitEmailSender(IConfiguration configuration, ILogger<MailKitEmailSender> logger)
    {
        _logger = logger;
        var section = configuration.GetSection("Smtp");
        _host = section["Host"] ?? "localhost";
        _port = section.GetValue("Port", 1025);
        _useSsl = section.GetValue("UseSsl", false);
        _username = section["Username"] ?? "";
        _password = section["Password"] ?? "";
        _from = section["From"] ?? "no-reply@harmony.local";
        _fromName = section["FromName"] ?? "Harmony";
    }

    public async Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string textBody,
        CancellationToken ct = default
    )
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_fromName, _from));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody, TextBody = textBody }.ToMessageBody();

        try
        {
            using var client = new SmtpClient();
            var socketOptions = _useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None;
            await client.ConnectAsync(_host, _port, socketOptions, ct);

            if (!string.IsNullOrWhiteSpace(_username))
                await client.AuthenticateAsync(_username, _password, ct);

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email: failed to send \"{Subject}\" to {ToEmail}", subject, toEmail);
            throw;
        }
    }
}
