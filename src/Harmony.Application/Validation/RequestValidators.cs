using FluentValidation;
using Harmony.Application.DTOs.Requests;

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
        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("Message content must not be empty.")
            .MaximumLength(2000).WithMessage("Message content must be 2000 characters or fewer.");
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
