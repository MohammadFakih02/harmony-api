using Harmony.Core.Domain.Entities;

namespace Harmony.Core.Services;

public interface IJwtService
{
    string GenerateAccessToken(User user);

    string GenerateRefreshToken();

    string HashRefreshToken(string token);
}
