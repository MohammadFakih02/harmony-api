using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Harmony.Core.Domain.Entities;
using Harmony.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Harmony.UnitTests.Services;

public class JwtServiceTests
{
    private readonly JwtService _sut;
    private const string TestKey = "test-super-secret-key-minimum-32-characters-long";

    public JwtServiceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = TestKey,
                    ["Jwt:Issuer"] = "harmony-api",
                    ["Jwt:Audience"] = "harmony-client",
                    ["Jwt:AccessTokenExpiryMinutes"] = "15",
                }
            )
            .Build();

        _sut = new JwtService(config);
    }

    // --- GenerateAccessToken ---

    [Fact]
    public void GenerateAccessToken_ShouldReturnNonEmptyString()
    {
        var user = BuildUser();

        var token = _sut.GenerateAccessToken(user);

        token.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GenerateAccessToken_ShouldBeValidJwt()
    {
        var user = BuildUser();

        var token = _sut.GenerateAccessToken(user);

        var handler = new JwtSecurityTokenHandler();
        handler.CanReadToken(token).Should().BeTrue();
    }

    [Fact]
    public void GenerateAccessToken_ShouldContainCorrectClaims()
    {
        var user = BuildUser(id: 123, userName: "testuser");

        var token = _sut.GenerateAccessToken(user);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        jwt.Subject.Should().Be("123");
        jwt.Claims.First(c => c.Type == "unique_name").Value.Should().Be("testuser");
        jwt.Claims.FirstOrDefault(c => c.Type == "jti").Should().NotBeNull();
    }

    [Fact]
    public void GenerateAccessToken_ShouldHaveCorrectIssuerAndAudience()
    {
        var user = BuildUser();

        var token = _sut.GenerateAccessToken(user);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        jwt.Issuer.Should().Be("harmony-api");
        jwt.Audiences.Should().Contain("harmony-client");
    }

    [Fact]
    public void GenerateAccessToken_ShouldExpireIn15Minutes()
    {
        var user = BuildUser();
        var before = DateTime.UtcNow;

        var token = _sut.GenerateAccessToken(user);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        jwt.ValidTo.Should().BeCloseTo(before.AddMinutes(15), precision: TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void GenerateAccessToken_ShouldGenerateUniqueJtiPerCall()
    {
        var user = BuildUser();

        var token1 = _sut.GenerateAccessToken(user);
        var token2 = _sut.GenerateAccessToken(user);

        var handler = new JwtSecurityTokenHandler();
        var jti1 = handler.ReadJwtToken(token1).Claims.First(c => c.Type == "jti").Value;
        var jti2 = handler.ReadJwtToken(token2).Claims.First(c => c.Type == "jti").Value;

        jti1.Should().NotBe(jti2);
    }

    // --- GenerateRefreshToken ---

    [Fact]
    public void GenerateRefreshToken_ShouldReturnNonEmptyString()
    {
        var token = _sut.GenerateRefreshToken();

        token.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GenerateRefreshToken_ShouldBeBase64()
    {
        var token = _sut.GenerateRefreshToken();

        var act = () => Convert.FromBase64String(token);
        act.Should().NotThrow();
    }

    [Fact]
    public void GenerateRefreshToken_ShouldBe64BytesWhenDecoded()
    {
        var token = _sut.GenerateRefreshToken();

        var bytes = Convert.FromBase64String(token);
        bytes.Should().HaveCount(64);
    }

    [Fact]
    public void GenerateRefreshToken_ShouldGenerateUniqueTokensEachCall()
    {
        var token1 = _sut.GenerateRefreshToken();
        var token2 = _sut.GenerateRefreshToken();

        token1.Should().NotBe(token2);
    }

    // --- HashRefreshToken ---

    [Fact]
    public void HashRefreshToken_ShouldReturnNonEmptyString()
    {
        var hash = _sut.HashRefreshToken("some-token");

        hash.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void HashRefreshToken_ShouldBeDeterministic()
    {
        var hash1 = _sut.HashRefreshToken("same-token");
        var hash2 = _sut.HashRefreshToken("same-token");

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void HashRefreshToken_ShouldProduceDifferentHashesForDifferentInputs()
    {
        var hash1 = _sut.HashRefreshToken("token-a");
        var hash2 = _sut.HashRefreshToken("token-b");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void HashRefreshToken_ShouldNotReturnOriginalToken()
    {
        var token = "my-raw-refresh-token";

        var hash = _sut.HashRefreshToken(token);

        hash.Should().NotBe(token);
    }

    // --- Helpers ---

    private static User BuildUser(long id = 1, string userName = "user") =>
        new()
        {
            Id = id,
            UserName = userName,
            Email = $"{userName}@test.com",
        };
}
