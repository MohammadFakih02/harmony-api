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
        RuleFor(x => x.Identifier).NotEmpty().WithMessage("Email or username is required.");
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
    // Cap an expiry at 24h — the longest "clear after" option the UI offers.
    private const int MaxExpiryMinutes = 24 * 60;

    public UpdateStatusRequestValidator()
    {
        RuleFor(x => x.Status)
            .NotEmpty().WithMessage("Status is required.")
            .Must(PresenceStatus.IsValidPreferred)
            .WithMessage("Status must be one of: online, away, dnd, invisible.");

        RuleFor(x => x.ExpiresInMinutes!.Value)
            .InclusiveBetween(1, MaxExpiryMinutes)
            .When(x => x.ExpiresInMinutes.HasValue)
            .WithMessage($"Expiry must be between 1 and {MaxExpiryMinutes} minutes.");
    }
}

public sealed class UpdateCustomStatusRequestValidator
    : AbstractValidator<UpdateCustomStatusRequest>
{
    private const int MaxExpiryMinutes = 24 * 60;

    public UpdateCustomStatusRequestValidator()
    {
        RuleFor(x => x.Message!)
            .MaximumLength(128).WithMessage("Custom status must be 128 characters or fewer.")
            .When(x => x.Message is not null);

        RuleFor(x => x.ExpiresInMinutes!.Value)
            .InclusiveBetween(1, MaxExpiryMinutes)
            .When(x => x.ExpiresInMinutes.HasValue)
            .WithMessage($"Expiry must be between 1 and {MaxExpiryMinutes} minutes.");
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

public sealed class TimeoutMemberRequestValidator : AbstractValidator<TimeoutMemberRequest>
{
    // Discord's hard cap on a member timeout.
    public const long MaxTimeoutSeconds = 28L * 24 * 60 * 60;

    public TimeoutMemberRequestValidator()
    {
        RuleFor(x => x.DurationSeconds)
            .InclusiveBetween(1, MaxTimeoutSeconds)
            .WithMessage($"Timeout duration must be between 1 second and {MaxTimeoutSeconds} seconds (28 days).");
    }
}

public sealed class SetNicknameRequestValidator : AbstractValidator<SetNicknameRequest>
{
    // Discord's nickname length cap; shared by the friend-nickname endpoint too.
    public const int MaxNicknameLength = 32;

    public SetNicknameRequestValidator()
    {
        // Null/blank is valid — it clears the nickname back to the username.
        RuleFor(x => x.Nickname!)
            .MaximumLength(MaxNicknameLength)
            .WithMessage($"Nickname must be {MaxNicknameLength} characters or fewer.")
            .When(x => x.Nickname is not null);
    }
}

public sealed class CreateRoleRequestValidator : AbstractValidator<CreateRoleRequest>
{
    public CreateRoleRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Role name is required.")
            .MaximumLength(100).WithMessage("Role name must be 100 characters or fewer.");
    }
}

public sealed class UpdateRoleRequestValidator : AbstractValidator<UpdateRoleRequest>
{
    public UpdateRoleRequestValidator()
    {
        RuleFor(x => x.Name!)
            .NotEmpty().WithMessage("Role name must not be empty.")
            .MaximumLength(100).WithMessage("Role name must be 100 characters or fewer.")
            .When(x => x.Name is not null);
    }
}

public sealed class CreateInviteRequestValidator : AbstractValidator<CreateInviteRequest>
{
    public CreateInviteRequestValidator()
    {
        // Shape only — that the channel (when given) actually belongs to the guild is semantic
        // and lives in GuildInvitesController. ChannelId is optional (null = guild-level invite).
        RuleFor(x => x.ChannelId!.Value)
            .GreaterThan(0).WithMessage("ChannelId must be a valid id.")
            .When(x => x.ChannelId.HasValue);

        RuleFor(x => x.MaxUses!.Value)
            .InclusiveBetween(1, 1000).WithMessage("Max uses must be between 1 and 1000.")
            .When(x => x.MaxUses.HasValue);

        RuleFor(x => x.ExpiresInSeconds!.Value)
            .InclusiveBetween(1, 2592000).WithMessage("Expiry must be between 1 second and 30 days.")
            .When(x => x.ExpiresInSeconds.HasValue);
    }
}
