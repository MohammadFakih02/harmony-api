namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// SDK-free seam over SMTP email delivery — the Infrastructure implementation (MailKit) is the
/// only code touching the mail library, same containment pattern as IWebPushSender/IFileStorageService.
/// Callers always supply both an HTML and a plain-text body (mail clients that block/strip HTML
/// still get a readable message).
/// </summary>
public interface IEmailSender
{
    Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string textBody,
        CancellationToken ct = default
    );
}
