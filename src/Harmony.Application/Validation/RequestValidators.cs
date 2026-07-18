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

public sealed class ConfirmEmailRequestValidator : AbstractValidator<ConfirmEmailRequest>
{
    public ConfirmEmailRequestValidator()
    {
        RuleFor(x => x.UserId).NotEmpty().WithMessage("UserId is required.");
        RuleFor(x => x.Token).NotEmpty().WithMessage("Token is required.");
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

public sealed class Verify2faRequestValidator : AbstractValidator<Verify2faRequest>
{
    public Verify2faRequestValidator()
    {
        RuleFor(x => x.ChallengeToken).NotEmpty().WithMessage("ChallengeToken is required.");
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Code is required.")
            .Matches("^[0-9]{6}$").WithMessage("Code must be exactly 6 digits.");
    }
}

public sealed class Resend2faRequestValidator : AbstractValidator<Resend2faRequest>
{
    public Resend2faRequestValidator()
    {
        RuleFor(x => x.ChallengeToken).NotEmpty().WithMessage("ChallengeToken is required.");
    }
}

public sealed class Enable2faRequestValidator : AbstractValidator<Enable2faRequest>
{
    public Enable2faRequestValidator()
    {
        RuleFor(x => x.Password).NotEmpty().WithMessage("Password is required.");
    }
}

public sealed class Confirm2faRequestValidator : AbstractValidator<Confirm2faRequest>
{
    public Confirm2faRequestValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Code is required.")
            .Matches("^[0-9]{6}$").WithMessage("Code must be exactly 6 digits.");
    }
}

public sealed class Disable2faRequestValidator : AbstractValidator<Disable2faRequest>
{
    public Disable2faRequestValidator()
    {
        RuleFor(x => x.Password).NotEmpty().WithMessage("Password is required.");
    }
}

public sealed class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Invalid email format.");
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.UserId).NotEmpty().WithMessage("UserId is required.");
        RuleFor(x => x.Token).NotEmpty().WithMessage("Token is required.");
        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(8).WithMessage("Password must be at least 8 characters.");
    }
}

public sealed class GoogleLoginRequestValidator : AbstractValidator<GoogleLoginRequest>
{
    public GoogleLoginRequestValidator()
    {
        RuleFor(x => x.IdToken).NotEmpty().WithMessage("IdToken is required.");
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().WithMessage("Current password is required.");
        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(8).WithMessage("Password must be at least 8 characters.");
        // Code is only present on the 2FA step-up follow-up call (D20) — optional, but must be a
        // 6-digit code when supplied.
        RuleFor(x => x.Code)
            .Matches("^[0-9]{6}$").WithMessage("Code must be exactly 6 digits.")
            .When(x => x.Code is not null);
    }
}

public sealed class SetPasswordRequestValidator : AbstractValidator<SetPasswordRequest>
{
    public SetPasswordRequestValidator()
    {
        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(8).WithMessage("Password must be at least 8 characters.");
    }
}

public sealed class ChangeEmailRequestValidator : AbstractValidator<ChangeEmailRequest>
{
    public ChangeEmailRequestValidator()
    {
        RuleFor(x => x.Password).NotEmpty().WithMessage("Password is required.");
        RuleFor(x => x.NewEmail)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Invalid email format.");
        // Code is only present on the 2FA step-up follow-up call (D20) — optional, but must be a
        // 6-digit code when supplied.
        RuleFor(x => x.Code)
            .Matches("^[0-9]{6}$").WithMessage("Code must be exactly 6 digits.")
            .When(x => x.Code is not null);
    }
}

public sealed class ConfirmEmailChangeRequestValidator : AbstractValidator<ConfirmEmailChangeRequest>
{
    public ConfirmEmailChangeRequestValidator()
    {
        RuleFor(x => x.UserId).NotEmpty().WithMessage("UserId is required.");
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Invalid email format.");
        RuleFor(x => x.Token).NotEmpty().WithMessage("Token is required.");
    }
}

public sealed class ChangeUsernameRequestValidator : AbstractValidator<ChangeUsernameRequest>
{
    public ChangeUsernameRequestValidator()
    {
        RuleFor(x => x.Password).NotEmpty().WithMessage("Password is required.");
        RuleFor(x => x.NewUsername)
            .NotEmpty().WithMessage("Username is required.")
            .Length(2, 32).WithMessage("Username must be between 2 and 32 characters.");
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

public sealed class SavePushSubscriptionRequestValidator
    : AbstractValidator<SavePushSubscriptionRequest>
{
    public SavePushSubscriptionRequestValidator()
    {
        // Shape only — the endpoint is opaque push-service state; the only real proof of
        // validity is a successful delivery (dead endpoints get pruned on 404/410 anyway).
        RuleFor(x => x.Endpoint)
            .NotEmpty().WithMessage("Endpoint is required.")
            .MaximumLength(2048).WithMessage("Endpoint must be 2048 characters or fewer.")
            .Must(e => Uri.TryCreate(e, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            .WithMessage("Endpoint must be an absolute https URL.");

        RuleFor(x => x.P256dh).NotEmpty().WithMessage("P256dh key is required.");
        RuleFor(x => x.AuthKey).NotEmpty().WithMessage("Auth key is required.");
    }
}

/// <summary>
/// PATCH /api/users/me. Every field is optional (null = leave unchanged), so each rule is
/// <c>.When(...is not null)</c>. Username rename lives on its own password-gated endpoint
/// (POST /api/auth/change-username, Stage E) — not here. Bio is <c>text</c> with no column limit,
/// so this cap is the only thing standing between a client and an arbitrarily large profile.
/// BannerColor/DateOfBirth *format* is parsed in UsersController (it owns the
/// clear-on-empty-string semantics) — shape-only here.
/// </summary>
public sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public const int MaxBioLength = 512;

    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.Bio!)
            .MaximumLength(MaxBioLength)
            .WithMessage($"Bio must be {MaxBioLength} characters or fewer.")
            .When(x => x.Bio is not null);

        RuleFor(x => x.StatusMessage!)
            .MaximumLength(128).WithMessage("Custom status must be 128 characters or fewer.")
            .When(x => x.StatusMessage is not null);
    }
}

public sealed class CreateGuildRequestValidator : AbstractValidator<CreateGuildRequest>
{
    // Matches the name column; description is `text`, so its cap exists only here.
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 1024;

    public CreateGuildRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Server name is required.")
            .Length(2, MaxNameLength)
            .WithMessage($"Server name must be between 2 and {MaxNameLength} characters.");

        RuleFor(x => x.Description!)
            .MaximumLength(MaxDescriptionLength)
            .WithMessage($"Description must be {MaxDescriptionLength} characters or fewer.")
            .When(x => x.Description is not null);
    }
}

public sealed class UpdateGuildRequestValidator : AbstractValidator<UpdateGuildRequest>
{
    public UpdateGuildRequestValidator()
    {
        RuleFor(x => x.Name!)
            .NotEmpty().WithMessage("Server name must not be empty.")
            .Length(2, CreateGuildRequestValidator.MaxNameLength)
            .WithMessage($"Server name must be between 2 and {CreateGuildRequestValidator.MaxNameLength} characters.")
            .When(x => x.Name is not null);

        RuleFor(x => x.Description!)
            .MaximumLength(CreateGuildRequestValidator.MaxDescriptionLength)
            .WithMessage($"Description must be {CreateGuildRequestValidator.MaxDescriptionLength} characters or fewer.")
            .When(x => x.Description is not null);
    }
}

public sealed class UpdateGuildWelcomeRequestValidator : AbstractValidator<UpdateGuildWelcomeRequest>
{
    public UpdateGuildWelcomeRequestValidator()
    {
        // Null/blank is valid (falls back to the built-in greeting). 2000 matches both the column
        // and the message-content cap this text is ultimately posted as.
        RuleFor(x => x.WelcomeMessage!)
            .MaximumLength(2000).WithMessage("Welcome message must be 2000 characters or fewer.")
            .When(x => x.WelcomeMessage is not null);

        // That the channel belongs to this guild is semantic — GuildsController checks it.
        RuleFor(x => x.WelcomeChannelId!.Value)
            .GreaterThan(0).WithMessage("WelcomeChannelId must be a valid id.")
            .When(x => x.WelcomeChannelId.HasValue);
    }
}

/// <summary>
/// Channel shape rules. Type validity, the voice-only bitrate/user-limit ranges, and the
/// category-belongs-to-this-guild check are all semantic and stay in ChannelsController.
/// </summary>
public sealed class CreateChannelRequestValidator : AbstractValidator<CreateChannelRequest>
{
    public const int MaxNameLength = 100;
    public const int MaxTopicLength = 1024;

    /// <summary>Discord's slowmode ceiling (6 hours). Unbounded, this could freeze a channel forever.</summary>
    public const int MaxSlowmodeSeconds = 21600;

    public CreateChannelRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Channel name is required.")
            .MaximumLength(MaxNameLength)
            .WithMessage($"Channel name must be {MaxNameLength} characters or fewer.");

        RuleFor(x => x.Topic!)
            .MaximumLength(MaxTopicLength)
            .WithMessage($"Channel topic must be {MaxTopicLength} characters or fewer.")
            .When(x => x.Topic is not null);

        RuleFor(x => x.SlowmodeSeconds)
            .InclusiveBetween(0, MaxSlowmodeSeconds)
            .WithMessage($"Slowmode must be between 0 and {MaxSlowmodeSeconds} seconds (6 hours).");

        RuleFor(x => x.Position)
            .GreaterThanOrEqualTo(0).WithMessage("Position must not be negative.");
    }
}

public sealed class UpdateChannelRequestValidator : AbstractValidator<UpdateChannelRequest>
{
    public UpdateChannelRequestValidator()
    {
        RuleFor(x => x.Name!)
            .NotEmpty().WithMessage("Channel name must not be empty.")
            .MaximumLength(CreateChannelRequestValidator.MaxNameLength)
            .WithMessage($"Channel name must be {CreateChannelRequestValidator.MaxNameLength} characters or fewer.")
            .When(x => x.Name is not null);

        RuleFor(x => x.Topic!)
            .MaximumLength(CreateChannelRequestValidator.MaxTopicLength)
            .WithMessage($"Channel topic must be {CreateChannelRequestValidator.MaxTopicLength} characters or fewer.")
            .When(x => x.Topic is not null);

        RuleFor(x => x.SlowmodeSeconds!.Value)
            .InclusiveBetween(0, CreateChannelRequestValidator.MaxSlowmodeSeconds)
            .WithMessage($"Slowmode must be between 0 and {CreateChannelRequestValidator.MaxSlowmodeSeconds} seconds (6 hours).")
            .When(x => x.SlowmodeSeconds.HasValue);
    }
}
