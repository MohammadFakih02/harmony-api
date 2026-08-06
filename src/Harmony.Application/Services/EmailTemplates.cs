using System.Net;

namespace Harmony.Application.Services;

/// <summary>
/// Static HTML/text builders for every transactional email Harmony sends. No templating engine —
/// table-based markup styled to match the client's default Violet · Dark theme so the emails look
/// like the app. Layout/spacing rides HTML attributes (width/align/bgcolor, td padding) rather
/// than the CSS properties email clients handle worst (margin, background shorthand, display,
/// max-width, word-break, text-align) — that's what keeps Mailpit's HTML compatibility check
/// happy, so prefer extending the row helpers over reintroducing those properties.
/// </summary>
public static class EmailTemplates
{
    private const string BgOuter = "#0b0a10";
    private const string BgCard = "#15141b";
    private const string BgCode = "#1e1c26";
    private const string Border = "#363247";
    private const string Divider = "#26232f";
    private const string TextPrimary = "#f4f3f7";
    private const string TextMuted = "#a29fb1";
    private const string TextFaint = "#9490a2";
    private const string Accent = "#8b5cf6";
    private const string AccentLight = "#a78bfa";
    // The app's --color-accent-muted (rgba(139,92,246,.15)) pre-composited over the card colour —
    // email clients don't do alpha reliably, so the flattened hex ships instead.
    private const string AccentMuted = "#261e3a";
    private const string FontStack = "'Inter', -apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif";
    private const string MonoStack = "'SFMono-Regular', Consolas, 'Liberation Mono', Menlo, monospace";

    public static (string Subject, string Html, string Text) VerifyEmail(string username, string link)
    {
        const string subject = "Verify your email · Harmony";
        var html = Layout(
            "Verify your email",
            Paragraph(
                $"Hi {Name(username)}, confirm this is your email address to finish setting up your Harmony account."
            ),
            ButtonRow("Verify Email", link),
            FinePrintRow(
                "If you didn't create a Harmony account, you can safely ignore this email.<br />"
                    + FallbackLink(link, "open the verification link")
            )
        );
        var text =
            $"Hi {username},\n\n"
            + "Confirm this is your email address to finish setting up your Harmony account:\n\n"
            + $"{link}\n\n"
            + "If you didn't create a Harmony account, you can safely ignore this email.";
        return (subject, html, text);
    }

    public static (string Subject, string Html, string Text) TwoFactorCode(string username, string code)
    {
        var subject = $"Your Harmony login code: {code}";
        var html = Layout(
            "Your login code",
            Paragraph($"Hi {Name(username)}, enter this code to finish signing in to Harmony:"),
            CodeRow(code),
            FinePrintRow(
                "This code expires in 10 minutes. If you didn't try to sign in, you can safely ignore this email."
            )
        );
        var text =
            $"Hi {username},\n\n"
            + $"Enter this code to finish signing in to Harmony: {code}\n\n"
            + "This code expires in 10 minutes. If you didn't try to sign in, you can safely ignore this email.";
        return (subject, html, text);
    }

    public static (string Subject, string Html, string Text) ResetPassword(string username, string link)
    {
        const string subject = "Reset your password · Harmony";
        var html = Layout(
            "Reset your password",
            Paragraph(
                $"Hi {Name(username)}, we received a request to reset your Harmony password. Click below to choose a new one."
            ),
            ButtonRow("Reset Password", link),
            FinePrintRow(
                "If you didn't request this, you can safely ignore this email — your password won't change.<br />"
                    + FallbackLink(link, "open the reset link")
            )
        );
        var text =
            $"Hi {username},\n\n"
            + "We received a request to reset your Harmony password. Open this link to choose a new one:\n\n"
            + $"{link}\n\n"
            + "If you didn't request this, you can safely ignore this email — your password won't change.";
        return (subject, html, text);
    }

    /// <summary>Step-up code for a change-password request on a 2FA-enabled account (D20) —
    /// distinct from <see cref="TwoFactorCode"/> (login) so the email accurately says why the code
    /// was sent.</summary>
    public static (string Subject, string Html, string Text) ChangePasswordCode(string username, string code)
    {
        var subject = $"Confirm your password change: {code}";
        var html = Layout(
            "Confirm your password change",
            Paragraph($"Hi {Name(username)}, enter this code to confirm changing your Harmony password:"),
            CodeRow(code),
            FinePrintRow(
                "This code expires in 10 minutes. If you didn't request this, you can safely ignore this email — your password won't change."
            )
        );
        var text =
            $"Hi {username},\n\n"
            + $"Enter this code to confirm changing your Harmony password: {code}\n\n"
            + "This code expires in 10 minutes. If you didn't request this, you can safely ignore this email — your password won't change.";
        return (subject, html, text);
    }

    /// <summary>Step-up code for a change-email request on a 2FA-enabled account (D20) — sent
    /// BEFORE the actual <see cref="ChangeEmail"/> confirmation link, and to the OLD address (the
    /// new one hasn't been proven yet at this point).</summary>
    public static (string Subject, string Html, string Text) ChangeEmailCode(string username, string code)
    {
        var subject = $"Confirm your email change: {code}";
        var html = Layout(
            "Confirm your email change",
            Paragraph(
                $"Hi {Name(username)}, enter this code to confirm changing your Harmony account's email:"
            ),
            CodeRow(code),
            FinePrintRow(
                "This code expires in 10 minutes. If you didn't request this, you can safely ignore this email — your email won't change."
            )
        );
        var text =
            $"Hi {username},\n\n"
            + $"Enter this code to confirm changing your Harmony account's email: {code}\n\n"
            + "This code expires in 10 minutes. If you didn't request this, you can safely ignore this email — your email won't change.";
        return (subject, html, text);
    }

    public static (string Subject, string Html, string Text) ChangeEmail(string username, string link)
    {
        const string subject = "Confirm your new email · Harmony";
        var html = Layout(
            "Confirm your new email",
            Paragraph(
                $"Hi {Name(username)}, confirm this address to finish changing your Harmony account's email."
            ),
            ButtonRow("Confirm New Email", link),
            FinePrintRow(
                "Your email won't change until you confirm. If you didn't request this, you can safely ignore this email.<br />"
                    + FallbackLink(link, "open the confirmation link")
            )
        );
        var text =
            $"Hi {username},\n\n"
            + "Confirm this address to finish changing your Harmony account's email:\n\n"
            + $"{link}\n\n"
            + "Your email won't change until you confirm. If you didn't request this, you can safely ignore this email.";
        return (subject, html, text);
    }

    /// <summary>The shared dark-themed shell, mirroring the app's login card: full-bleed backdrop,
    /// a centered 600px card opening with the ☯ badge + letterspaced wordmark + heading (the same
    /// stack the login page renders), then the caller's rows, then the outside-the-card footer.</summary>
    private static string Layout(string title, params string[] rows) =>
        $"""
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>{Encode(title)}</title>
        </head>
        <body bgcolor="{BgOuter}" style="margin:0; padding:0;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="{BgOuter}">
                <tr>
                    <td align="center" style="padding:48px 16px;">
                        <table role="presentation" width="600" cellpadding="0" cellspacing="0" border="0" bgcolor="{BgCard}"
                               style="width:100%; max-width:600px; border:1px solid {Border}; border-radius:16px;">
                            <tr>
                                <td align="center" style="padding:44px 48px 18px;">
                                    <table role="presentation" cellpadding="0" cellspacing="0" border="0">
                                        <tr>
                                            <td align="center" bgcolor="{AccentMuted}"
                                                style="border-radius:18px; padding:16px 21px; font-family:{FontStack};
                                                       font-size:32px; line-height:1; color:{AccentLight};">&#9775;&#65038;</td>
                                        </tr>
                                    </table>
                                </td>
                            </tr>
                            <tr>
                                <td align="center" style="padding:0 48px 26px; font-family:{FontStack};
                                        font-size:12px; font-weight:700; letter-spacing:4px; color:{Accent};">
                                    HARMONY
                                </td>
                            </tr>
                            <tr>
                                <td align="center" style="padding:0 48px 10px; font-family:{FontStack};
                                        font-size:22px; font-weight:700; color:{TextPrimary};">
                                    {Encode(title)}
                                </td>
                            </tr>
                            {string.Join("\n", rows)}
                        </table>
                        <table role="presentation" width="600" cellpadding="0" cellspacing="0" border="0"
                               style="width:100%; max-width:600px;">
                            <tr>
                                <td align="center" style="padding:24px 8px; font-family:{FontStack}; font-size:12px; color:{TextFaint};">
                                    Harmony &middot; This is an automated message &mdash; please don't reply.
                                </td>
                            </tr>
                        </table>
                    </td>
                </tr>
            </table>
        </body>
        </html>
        """;

    /// <summary>The centered body-copy row every template opens with.</summary>
    private static string Paragraph(string innerHtml) =>
        $"""
        <tr>
            <td align="center" style="padding:4px 48px 30px; font-family:{FontStack};
                    font-size:15px; line-height:1.65; color:{TextMuted};">
                {innerHtml}
            </td>
        </tr>
        """;

    /// <summary>Full-width CTA — the same block-level primary button the app's auth forms use.</summary>
    private static string ButtonRow(string label, string url) =>
        $"""
        <tr>
            <td style="padding:0 48px 36px;">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">
                    <tr>
                        <td align="center" bgcolor="{Accent}" style="border-radius:10px; padding:14px 24px;">
                            <a href="{Encode(url)}" target="_blank"
                               style="font-family:{FontStack}; font-size:15px; font-weight:600;
                                      color:#ffffff; text-decoration:none;">{Encode(label)}</a>
                        </td>
                    </tr>
                </table>
            </td>
        </tr>
        """;

    private static string CodeRow(string code) =>
        $"""
        <tr>
            <td align="center" style="padding:2px 48px 34px;">
                <table role="presentation" cellpadding="0" cellspacing="0" border="0">
                    <tr>
                        <td align="center" bgcolor="{BgCode}"
                            style="border:1px solid {Border}; border-radius:10px; padding:18px 26px 18px 36px;
                                   font-family:{MonoStack}; font-size:32px; font-weight:600;
                                   letter-spacing:10px; color:{AccentLight};">{Encode(code)}</td>
                    </tr>
                </table>
            </td>
        </tr>
        """;

    /// <summary>Closing fine print, separated from the body by a full-width divider (border-top
    /// here, mirroring the header's border-bottom — no spacer-cell tricks needed).</summary>
    private static string FinePrintRow(string innerHtml) =>
        $"""
        <tr>
            <td align="center" style="padding:24px 48px 32px; border-top:1px solid {Divider};
                    font-family:{FontStack}; font-size:12px; line-height:1.7; color:{TextFaint};">
                {innerHtml}
            </td>
        </tr>
        """;

    /// <summary>The clients-that-mangle-the-button fallback: a short styled link instead of the
    /// raw URL (a printed URL needs word-break, the single worst-supported property in the
    /// compatibility data; the plain-text part still carries the full raw URL for copying).</summary>
    private static string FallbackLink(string url, string label) =>
        $"""Trouble with the button? <a href="{Encode(url)}" target="_blank" style="color:{AccentLight};">{Encode(label)}</a> instead.""";

    private static string Name(string username) =>
        $"""<span style="font-weight:600; color:{TextPrimary};">{Encode(username)}</span>""";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
