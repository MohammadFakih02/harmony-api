namespace Harmony.Application.Exceptions;

/// <summary>
/// Thrown when a request is well-formed but violates a business rule the caller could reasonably hit —
/// e.g. "verify your email first", "the @everyone role cannot be deleted", or an ASP.NET Identity
/// failure (weak password, duplicate name surfaced by the provider). Mapped to HTTP 400 by
/// <c>GlobalExceptionHandler</c>; its message is safe to return to the client.
/// </summary>
/// <remarks>
/// Replaces the earlier convention (audit A3) of throwing a bare <see cref="InvalidOperationException"/>
/// for these cases. That was fragile: the handler mapped <em>every</em> <see cref="InvalidOperationException"/>
/// to 400, so a genuine internal fault (a LINQ <c>.Single()</c> mismatch, a clock-moved-backward guard,
/// any library invariant break) was silently disguised as a client error and logged only at Warning.
/// A dedicated type lets business-rule rejections stay 400 while real faults correctly surface as 500.
/// This mirrors <see cref="ConflictException"/> (409) — same idea, different status.
/// </remarks>
public class DomainRuleException : Exception
{
    public DomainRuleException(string message)
        : base(message) { }

    public DomainRuleException(string message, Exception inner)
        : base(message, inner) { }
}
