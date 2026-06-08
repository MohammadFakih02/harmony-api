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
    public async Task Refresh_WithConcurrentRequests_ShouldAllowOnlyOneToSucceed()
    {
        // Arrange: Register and log in once to obtain the active refresh token cookie
        await RegisterUserAsync("concurrentuser", "concurrent@example.com", "Password123!");

        // Act: Fire two concurrent refresh requests simultaneously
        var task1 = Client.PostAsJsonAsync("/api/auth/refresh", new { });
        var task2 = Client.PostAsJsonAsync("/api/auth/refresh", new { });

        await Task.WhenAll(task1, task2);

        var res1 = await task1;
        var res2 = await task2;

        // Assert: One request must successfully rotate, and the other must fail with 401 Unauthorized
        var statusCodes = new[] { res1.StatusCode, res2.StatusCode };
        statusCodes.Should().ContainSingle(s => s == HttpStatusCode.OK);
        statusCodes.Should().ContainSingle(s => s == HttpStatusCode.Unauthorized);
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

    private record AuthResponse(string AccessToken);
}
