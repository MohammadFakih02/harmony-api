namespace Harmony.Application.Exceptions;

/// <summary>
/// Thrown when a request conflicts with existing state — e.g. a duplicate email/username on
/// registration or a credential change. Mapped to HTTP 409 by <c>GlobalExceptionHandler</c>.
/// Replaces the earlier fragile convention of throwing an <see cref="InvalidOperationException"/>
/// whose message happened to contain the word "already".
/// </summary>
public class ConflictException : Exception
{
    public ConflictException(string message)
        : base(message) { }

    public ConflictException(string message, Exception inner)
        : base(message, inner) { }
}
