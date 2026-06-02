using Harmony.Domain.Domain.Entities;

namespace Harmony.Application.Services;

public interface IJwtService
{
    string GenerateAccessToken(User user);

    string GenerateRefreshToken();

    string HashRefreshToken(string token);
}
