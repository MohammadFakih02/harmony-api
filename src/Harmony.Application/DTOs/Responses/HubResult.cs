namespace Harmony.Application.DTOs.Responses;

/// <summary>
/// Reusable, generic envelope representing the result of a real-time Hub action.
/// Eliminates exception-throwing for expected business-validation failures.
/// </summary>
public record HubResult<T>(bool Succeeded, T? Data, string? ErrorMessage);
