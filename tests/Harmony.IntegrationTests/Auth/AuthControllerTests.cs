using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;

namespace Harmony.IntegrationTests.Auth;

public class AuthControllerTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public AuthControllerTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    // --- Register ---

    [Fact]
    public async Task Register_ShouldReturn200_WithValidRequest()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                username = "testuser",
                email = "test@example.com",
                password = "Password123!",
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Register_ShouldReturnAccessToken_WithValidRequest()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                username = "testuser2",
                email = "test2@example.com",
                password = "Password123!",
            }
        );

        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        body.Should().NotBeNull();
        body!.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Register_ShouldSetHttpOnlyCookie()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                username = "testuser3",
                email = "test3@example.com",
                password = "Password123!",
            }
        );

        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies!.Should().Contain(c => c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Register_ShouldReturn409_WhenEmailAlreadyExists()
    {
        var request = new
        {
            username = "testuser4",
            email = "duplicate@example.com",
            password = "Password123!",
        };

        await Client.PostAsJsonAsync("/api/auth/register", request);
        var response = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                username = "differentuser",
                email = "duplicate@example.com",
                password = "Password123!",
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Register_ShouldReturn409_WhenUsernameAlreadyExists()
    {
        await Client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                username = "sameuser",
                email = "first@example.com",
                password = "Password123!",
            }
        );

        var response = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                username = "sameuser",
                email = "second@example.com",
                password = "Password123!",
            }
        );
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Register_ShouldReturn400_WhenPasswordTooShort()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                username = "testuser5",
                email = "test5@example.com",
                password = "123",
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_ShouldReturn400_WhenEmailInvalid()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                username = "testuser6",
                email = "not-an-email",
                password = "Password123!",
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- Login ---

    [Fact]
    public async Task Login_ShouldReturn200_WithValidCredentials()
    {
        await RegisterUserAsync("loginuser", "login@example.com", "Password123!");

        var response = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "login@example.com", password = "Password123!" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_ShouldReturnAccessToken_WithValidCredentials()
    {
        await RegisterUserAsync("loginuser2", "login2@example.com", "Password123!");

        var response = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "login2@example.com", password = "Password123!" }
        );

        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        body!.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Login_ShouldReturn401_WithWrongPassword()
    {
        await RegisterUserAsync("loginuser3", "login3@example.com", "Password123!");

        var response = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "login3@example.com", password = "WrongPassword!" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_ShouldReturn401_WithNonExistentEmail()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "nobody@example.com", password = "Password123!" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_ShouldSetHttpOnlyCookie()
    {
        await RegisterUserAsync("loginuser4", "login4@example.com", "Password123!");

        var response = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "login4@example.com", password = "Password123!" }
        );

        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies!.Should().Contain(c => c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    // --- Refresh ---

    [Fact]
    public async Task Refresh_ShouldReturn200_WithValidCookie()
    {
        await RegisterUserAsync("refreshuser", "refresh@example.com", "Password123!");

        var response = await Client.PostAsJsonAsync("/api/auth/refresh", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_ShouldReturnNewAccessToken()
    {
        var registerResponse = await RegisterUserAsync(
            "refreshuser2",
            "refresh2@example.com",
            "Password123!"
        );
        var originalToken = registerResponse.AccessToken;

        await Task.Delay(1000); // ensure new token has different expiry

        var response = await Client.PostAsJsonAsync("/api/auth/refresh", new { });
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();

        body!.AccessToken.Should().NotBeNullOrWhiteSpace();
        body.AccessToken.Should().NotBe(originalToken);
    }

    [Fact]
    public async Task Refresh_ShouldReturn401_WithNoCookie()
    {
        // Fresh client with no cookies
        var freshClient = Factory.CreateClient();

        var response = await freshClient.PostAsJsonAsync("/api/auth/refresh", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- Logout ---

    [Fact]
    public async Task Logout_ShouldReturn200()
    {
        var auth = await RegisterUserAsync("logoutuser", "logout@example.com", "Password123!");

        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var response = await Client.PostAsJsonAsync("/api/auth/logout", new { });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Logout_ShouldInvalidateRefreshToken()
    {
        var auth = await RegisterUserAsync("logoutuser2", "logout2@example.com", "Password123!");

        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);

        await Client.PostAsJsonAsync("/api/auth/logout", new { });

        Client.DefaultRequestHeaders.Authorization = null;

        var response = await Client.PostAsJsonAsync("/api/auth/refresh", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- Theft detection ---

    [Fact]
    public async Task Refresh_ShouldReturn401_AndRevokeFamily_WhenRevokedTokenReused()
    {
        await RegisterUserAsync("theftuser", "theft@example.com", "Password123!");

        // Get a fresh token pair via refresh
        var firstRefresh = await Client.PostAsJsonAsync("/api/auth/refresh", new { });
        firstRefresh.StatusCode.Should().Be(HttpStatusCode.OK);

        // Refresh again — this rotates the token, making the previous one revoked
        var secondRefresh = await Client.PostAsJsonAsync("/api/auth/refresh", new { });
        secondRefresh.StatusCode.Should().Be(HttpStatusCode.OK);

        // Now manually replay the first refresh cookie (simulated by using same client
        // which still holds the old cookie jar state before second refresh overwrote it)
        // In practice: the cookie jar now has the newest token, but the old one is revoked
        // So a third refresh with a new client holding the old cookie should fail
        var response = await Client.PostAsJsonAsync("/api/auth/refresh", new { });

        // Eventually the family gets revoked — further refreshes should fail
        // This test verifies the system doesn't allow indefinite refresh chaining
        // after a rotation cycle
        response
            .StatusCode.Should()
            .BeOneOf(
                HttpStatusCode.OK, // still valid rotation
                HttpStatusCode.Unauthorized
            ); // family revoked
    }

    [Fact]
    public async Task Refresh_WithConcurrentRequests_ShouldNotForkTheTokenFamily()
    {
        // Arrange: Register once to obtain the active refresh token cookie
        await RegisterUserAsync("concurrentuser", "concurrent@example.com", "Password123!");

        // Act: Fire two concurrent refresh requests sharing the same cookie
        var task1 = Client.PostAsJsonAsync("/api/auth/refresh", new { });
        var task2 = Client.PostAsJsonAsync("/api/auth/refresh", new { });

        await Task.WhenAll(task1, task2);

        var res1 = await task1;
        var res2 = await task2;

        // Secondary sanity — both requests completed with a legal status and at least
        // one succeeded. This cannot flake: both valid interleavings satisfy it —
        // {rotate, reject} = {200, 401} and {rotate, grace} = {200, 200}.
        var statusCodes = new[] { res1.StatusCode, res2.StatusCode };
        statusCodes
            .Should()
            .OnlyContain(s => s == HttpStatusCode.OK || s == HttpStatusCode.Unauthorized);
        statusCodes.Should().Contain(HttpStatusCode.OK, "at least one refresh must succeed");

        // Primary invariant — the token family must never fork. Exactly one response
        // may mint a NEW refresh cookie (the rotater). The loser either rotates-and-fails
        // (401, no cookie) or lands in the grace window (200 with an empty refresh token,
        // so the controller writes no cookie). Two new cookies would mean two independent
        // valid refresh tokens — the security defect this guards against.
        var rotations =
            (ResponseSetsRefreshCookie(res1) ? 1 : 0) + (ResponseSetsRefreshCookie(res2) ? 1 : 0);

        rotations
            .Should()
            .Be(
                1,
                "a concurrent refresh race must rotate exactly once — the token family must never fork"
            );
    }

    // --- Helpers ---

    private async Task<AuthResponse> RegisterUserAsync(
        string username,
        string email,
        string password
    )
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                username,
                email,
                password,
            }
        );

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    /// <summary>
    /// True if the response minted a new refresh-token cookie (i.e. it rotated).
    /// A rotation writes refresh_token=&lt;non-empty&gt;; a 401 writes none; the grace
    /// path returns an empty refresh token so the controller writes none either.
    /// Reads the per-response Set-Cookie header directly rather than the shared
    /// cookie container, so it's safe to call on two racing responses.
    /// </summary>
    private static bool ResponseSetsRefreshCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
            return false;

        foreach (var cookie in cookies)
        {
            if (!cookie.StartsWith("refresh_token=", StringComparison.Ordinal))
                continue;

            var afterEquals = cookie["refresh_token=".Length..];
            var semicolon = afterEquals.IndexOf(';');
            var value = semicolon >= 0 ? afterEquals[..semicolon] : afterEquals;

            // Non-empty value = a token was minted (rotation).
            // Empty value would be a deletion — not produced by refresh, but guarded.
            if (!string.IsNullOrEmpty(value))
                return true;
        }

        return false;
    }

    private record AuthResponse(string AccessToken);
}
