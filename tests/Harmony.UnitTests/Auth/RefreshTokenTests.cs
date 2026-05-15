using FluentAssertions;
using Harmony.Core.Domain.Entities;

namespace Harmony.UnitTests.Auth;

public class RefreshTokenTests
{
    // --- IsExpired ---

    [Fact]
    public void IsExpired_ShouldReturnFalse_WhenExpiryIsInFuture()
    {
        var token = BuildToken(expiresAt: DateTimeOffset.UtcNow.AddDays(1));

        token.IsExpired.Should().BeFalse();
    }

    [Fact]
    public void IsExpired_ShouldReturnTrue_WhenExpiryIsInPast()
    {
        var token = BuildToken(expiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        token.IsExpired.Should().BeTrue();
    }

    [Fact]
    public void IsExpired_ShouldReturnTrue_WhenExpiryIsNow()
    {
        var token = BuildToken(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));

        token.IsExpired.Should().BeTrue();
    }

    // --- IsActive ---

    [Fact]
    public void IsActive_ShouldReturnTrue_WhenNotRevokedAndNotExpired()
    {
        var token = BuildToken(expiresAt: DateTimeOffset.UtcNow.AddDays(1), revokedAt: null);

        token.IsActive.Should().BeTrue();
    }

    [Fact]
    public void IsActive_ShouldReturnFalse_WhenRevoked()
    {
        var token = BuildToken(
            expiresAt: DateTimeOffset.UtcNow.AddDays(1),
            revokedAt: DateTimeOffset.UtcNow.AddMinutes(-5)
        );

        token.IsActive.Should().BeFalse();
    }

    [Fact]
    public void IsActive_ShouldReturnFalse_WhenExpired()
    {
        var token = BuildToken(expiresAt: DateTimeOffset.UtcNow.AddDays(-1), revokedAt: null);

        token.IsActive.Should().BeFalse();
    }

    [Fact]
    public void IsActive_ShouldReturnFalse_WhenBothRevokedAndExpired()
    {
        var token = BuildToken(
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1),
            revokedAt: DateTimeOffset.UtcNow.AddDays(-2)
        );

        token.IsActive.Should().BeFalse();
    }

    // --- Family ---

    [Fact]
    public void FamilyId_ShouldGroupTokensFromSameLoginSession()
    {
        var familyId = Guid.NewGuid();

        var token1 = BuildToken(familyId: familyId);
        var token2 = BuildToken(familyId: familyId);

        token1.FamilyId.Should().Be(token2.FamilyId);
    }

    [Fact]
    public void FamilyId_ShouldDifferBetweenLoginSessions()
    {
        var token1 = BuildToken(familyId: Guid.NewGuid());
        var token2 = BuildToken(familyId: Guid.NewGuid());

        token1.FamilyId.Should().NotBe(token2.FamilyId);
    }

    // --- Theft detection simulation ---

    [Fact]
    public void TheftDetection_ReusingRevokedToken_ShouldIndicateFamilyCompromised()
    {
        var familyId = Guid.NewGuid();

        // Token was issued, used once (rotated = revoked), then someone tries to reuse it
        var revokedToken = BuildToken(
            familyId: familyId,
            expiresAt: DateTimeOffset.UtcNow.AddDays(1),
            revokedAt: DateTimeOffset.UtcNow.AddMinutes(-1)
        ); // rotated away

        // Simulate: token is not active but belongs to a family
        // Auth service would revoke all tokens in this family
        revokedToken.IsActive.Should().BeFalse();
        revokedToken.FamilyId.Should().Be(familyId);

        // All other tokens in the family should also be revoked
        var siblingToken = BuildToken(
            familyId: familyId,
            expiresAt: DateTimeOffset.UtcNow.AddDays(1),
            revokedAt: DateTimeOffset.UtcNow
        ); // revoked by theft detection

        siblingToken.IsActive.Should().BeFalse();
    }

    [Fact]
    public void TokenRotation_NewTokenShouldBeActive_OldTokenShouldBeRevoked()
    {
        var familyId = Guid.NewGuid();

        var oldToken = BuildToken(
            familyId: familyId,
            expiresAt: DateTimeOffset.UtcNow.AddDays(7),
            revokedAt: DateTimeOffset.UtcNow
        ); // revoked on rotation

        var newToken = BuildToken(
            familyId: familyId,
            expiresAt: DateTimeOffset.UtcNow.AddDays(7),
            revokedAt: null
        ); // new active token

        oldToken.IsActive.Should().BeFalse();
        newToken.IsActive.Should().BeTrue();
        oldToken.FamilyId.Should().Be(newToken.FamilyId);
    }

    // --- Helpers ---

    private static RefreshToken BuildToken(
        Guid? familyId = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? revokedAt = null
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = 1,
            TokenHash = "some-hash",
            FamilyId = familyId ?? Guid.NewGuid(),
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddDays(7),
            CreatedAt = 0,
            RevokedAt = revokedAt,
        };
}
