namespace Harmony.Domain.Interfaces.Services;

using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;

public interface IAuthService
{
    Task<(AuthResponse response, string rawRefreshToken)> RegisterAsync(
        RegisterRequest request,
        CancellationToken ct = default
    );

    Task<(AuthResponse response, string rawRefreshToken)> LoginAsync(
        LoginRequest request,
        CancellationToken ct = default
    );

    Task<(AuthResponse response, string rawRefreshToken)> RefreshAsync(
        string rawRefreshToken,
        CancellationToken ct = default
    );

    Task LogoutAsync(string rawRefreshToken, CancellationToken ct = default);
}
