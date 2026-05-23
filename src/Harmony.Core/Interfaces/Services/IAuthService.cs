namespace Harmony.Core.Interfaces.Services;

using Harmony.Core.DTOs.Requests;
using Harmony.Core.DTOs.Responses;

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
