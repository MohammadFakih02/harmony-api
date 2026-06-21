using FluentValidation;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.Interfaces.Services;

namespace Harmony.Application.Validation;

/// <summary>
/// FluentValidation validators for inbound request DTOs. These are the single source
/// of truth for request *shape* rules (length, presence, format). Semantic rules that
/// require I/O — "email already in use", "not a member", "channel not found" — stay in
/// the services where the data lives.
///
/// Run automatically for controller actions via the global ValidationActionFilter;
/// failures surface as 400 ProblemDetails (FluentValidation.ValidationException is also
/// mapped to 400 by GlobalExceptionHandler for any service-path ValidateAndThrow).
/// </summary>
public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Username)
            .NotEmpty().WithMessage("Username is required.")
            .Length(2, 32).WithMessage("Username must be between 2 and 32 characters.");

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Invalid email format.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(8).WithMessage("Password must be at least 8 characters.");
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("Email is required.");
        RuleFor(x => x.Password).NotEmpty().WithMessage("Password is required.");
    }
}

public sealed class SendMessageRequestValidator : AbstractValidator<SendMessageRequest>
{
    public SendMessageRequestValidator()
    {
        // Content is required only when the message has no attachments — an image-only message
        // (empty content + ≥1 attachment) is valid. The owned/confirmed/in-channel attachment
        // checks are semantic and live in MessageService.
        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("Message content must not be empty.")
            .When(x => x.AttachmentIds is null || x.AttachmentIds.Count == 0);

        RuleFor(x => x.Content)
            .MaximumLength(2000).WithMessage("Message content must be 2000 characters or fewer.");

        RuleFor(x => x.AttachmentIds!)
            .Must(a => a.Count <= Services.MessageService.MaxAttachments)
            .When(x => x.AttachmentIds is not null)
            .WithMessage($"A message may have at most {Services.MessageService.MaxAttachments} attachments.");
    }
}

public sealed class EditMessageRequestValidator : AbstractValidator<EditMessageRequest>
{
    public EditMessageRequestValidator()
    {
        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("Message content must not be empty.")
            .MaximumLength(2000).WithMessage("Message content must be 2000 characters or fewer.");
    }
}

public sealed class UpdateStatusRequestValidator : AbstractValidator<UpdateStatusRequest>
{
    public UpdateStatusRequestValidator()
    {
        RuleFor(x => x.Status)
            .NotEmpty().WithMessage("Status is required.")
            .Must(PresenceStatus.IsValidPreferred)
            .WithMessage("Status must be one of: online, away, dnd, invisible.");
    }
}

public sealed class PresignFileRequestValidator : AbstractValidator<PresignFileRequest>
{
    public PresignFileRequestValidator()
    {
        RuleFor(x => x.Filename)
            .NotEmpty().WithMessage("Filename is required.")
            .MaximumLength(256).WithMessage("Filename must be 256 characters or fewer.");

        RuleFor(x => x.ContentType)
            .NotEmpty().WithMessage("Content type is required.");

        // Upper bound only — the allowlist and any type-specific rules are semantic and live in
        // FileService. The shared cap is FileService.MaxFileSizeBytes to keep one source of truth.
        RuleFor(x => x.SizeBytes)
            .GreaterThan(0).WithMessage("File size must be greater than zero.")
            .LessThanOrEqualTo(Services.FileService.MaxFileSizeBytes)
            .WithMessage("File exceeds the maximum allowed size.");
    }
}

public sealed class CreateMuteRequestValidator : AbstractValidator<CreateMuteRequest>
{
    public CreateMuteRequestValidator()
    {
        RuleFor(x => x.TargetType)
            .NotEmpty().WithMessage("Target type is required.")
            .Must(MuteTargetType.IsValid)
            .WithMessage("Target type must be one of: guild, channel, user.");

        RuleFor(x => x.TargetId)
            .GreaterThan(0).WithMessage("Target id is required.");

        // Lenient by design: a mute is a personal preference, so we don't verify the
        // target exists or that the caller is a member. The only temporal rule is that
        // a provided expiry must be in the future — a past expiry would be swept instantly.
        RuleFor(x => x.MutedUntil!.Value)
            .GreaterThan(_ => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            .When(x => x.MutedUntil.HasValue)
            .WithMessage("MutedUntil must be a future timestamp.");
    }
}

public sealed class SendFriendRequestRequestValidator : AbstractValidator<SendFriendRequestRequest>
{
    public SendFriendRequestRequestValidator()
    {
        // Shape only — whether the username resolves to a real, non-self, non-blocked
        // user is semantic and lives in FriendsController.
        RuleFor(x => x.Username)
            .NotEmpty().WithMessage("Username is required.")
            .Length(2, 32).WithMessage("Username must be between 2 and 32 characters.");
    }
}
