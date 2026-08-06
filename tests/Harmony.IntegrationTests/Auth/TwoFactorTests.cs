using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Web;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Harmony.IntegrationTests.Auth;

public class TwoFactorTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public TwoFactorTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    [Fact]
    public async Task EnableRequest_ShouldReturn400_WhenEmailNotVerified()
    {
        var (_, email, password) = await RegisterAsync("tfa1", "tfa1@example.com");
        AuthorizeAs(await LoginRawAsync(email, password));

        var response = await Client.PostAsJsonAsync("/api/auth/2fa/enable/request", new { password });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EnableConfirm_ShouldTurnOn2fa_WithValidCode()
    {
        var (email, password) = await SetUpVerifiedUserAsync("tfa2", "tfa2@example.com");
        var accessToken = await LoginRawAsync(email, password);
        AuthorizeAs(accessToken);

        var request = await Client.PostAsJsonAsync("/api/auth/2fa/enable/request", new { password });
        request.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var code = ExtractCode(LastEmailTo(email)!);
        var confirm = await Client.PostAsJsonAsync("/api/auth/2fa/enable/confirm", new { code });
        confirm.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // 2FA is now on — logging in again must return a challenge instead of tokens.
        var login = await LoginAsync(email, password);
        login.TwoFactorRequired.Should().BeTrue();
        login.AccessToken.Should().BeNull();
    }

    [Fact]
    public async Task Login_ShouldReturnChallenge_AndEmailACode_When2faEnabled()
    {
        var (email, password) = await EnableTwoFactorAsync("tfa3", "tfa3@example.com");

        var before = SentCount(email);
        var login = await LoginAsync(email, password);

        login.TwoFactorRequired.Should().BeTrue();
        login.ChallengeToken.Should().NotBeNullOrEmpty();
        login.AccessToken.Should().BeNull();
        SentCount(email).Should().BeGreaterThan(before);
    }

    [Fact]
    public async Task Verify2fa_ShouldIssueTokens_WithValidCode()
    {
        var (email, password) = await EnableTwoFactorAsync("tfa4", "tfa4@example.com");
        var login = await LoginAsync(email, password);
        var code = ExtractCode(LastEmailTo(email)!);

        var response = await Client.PostAsJsonAsync(
            "/api/auth/2fa/verify",
            new { challengeToken = login.ChallengeToken, code, rememberDevice = false }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        body.TwoFactorRequired.Should().BeFalse();
        body.AccessToken.Should().NotBeNullOrEmpty();
        HasCookie(response, "refresh_token").Should().BeTrue();
        HasCookie(response, "trusted_device").Should().BeFalse();
    }

    [Fact]
    public async Task Verify2fa_ShouldReturn401_WithWrongCode()
    {
        var (email, password) = await EnableTwoFactorAsync("tfa5", "tfa5@example.com");
        var login = await LoginAsync(email, password);

        var response = await Client.PostAsJsonAsync(
            "/api/auth/2fa/verify",
            new { challengeToken = login.ChallengeToken, code = "000000", rememberDevice = false }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Verify2fa_FiveWrongAttempts_ShouldLockTheChallenge_EvenForTheRightCode()
    {
        var (email, password) = await EnableTwoFactorAsync("tfa6", "tfa6@example.com");
        var login = await LoginAsync(email, password);
        var correctCode = ExtractCode(LastEmailTo(email)!);

        for (var i = 0; i < 5; i++)
        {
            var attempt = await Client.PostAsJsonAsync(
                "/api/auth/2fa/verify",
                new { challengeToken = login.ChallengeToken, code = "111111", rememberDevice = false }
            );
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // The challenge is now locked — even the correct code must be rejected.
        var lastAttempt = await Client.PostAsJsonAsync(
            "/api/auth/2fa/verify",
            new { challengeToken = login.ChallengeToken, code = correctCode, rememberDevice = false }
        );
        lastAttempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Verify2fa_ShouldReturn401_WithUnknownChallengeToken()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/2fa/verify",
            new { challengeToken = "not-a-real-token", code = "123456", rememberDevice = false }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Resend2fa_ShouldEmailAFreshCode_AndInvalidateTheOldOne()
    {
        var (email, password) = await EnableTwoFactorAsync("tfa7", "tfa7@example.com");
        var login = await LoginAsync(email, password);
        var firstCode = ExtractCode(LastEmailTo(email)!);
        var before = SentCount(email);

        var resend = await Client.PostAsJsonAsync(
            "/api/auth/2fa/resend",
            new { challengeToken = login.ChallengeToken }
        );
        resend.StatusCode.Should().Be(HttpStatusCode.NoContent);
        SentCount(email).Should().Be(before + 1);

        var newCode = ExtractCode(LastEmailTo(email)!);
        newCode.Should().NotBe(firstCode);

        // The old code no longer validates against the regenerated challenge.
        var oldCodeAttempt = await Client.PostAsJsonAsync(
            "/api/auth/2fa/verify",
            new { challengeToken = login.ChallengeToken, code = firstCode, rememberDevice = false }
        );
        oldCodeAttempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var newCodeAttempt = await Client.PostAsJsonAsync(
            "/api/auth/2fa/verify",
            new { challengeToken = login.ChallengeToken, code = newCode, rememberDevice = false }
        );
        newCodeAttempt.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Verify2fa_RememberDevice_ShouldSkipTheChallengeOnTheNextLogin()
    {
        var (email, password) = await EnableTwoFactorAsync("tfa8", "tfa8@example.com");
        var login = await LoginAsync(email, password);
        var code = ExtractCode(LastEmailTo(email)!);

        var verify = await Client.PostAsJsonAsync(
            "/api/auth/2fa/verify",
            new { challengeToken = login.ChallengeToken, code, rememberDevice = true }
        );
        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        HasCookie(verify, "trusted_device").Should().BeTrue();

        // Same Client == the trusted_device cookie rides along automatically (HandleCookies=true).
        var secondLogin = await LoginAsync(email, password);
        secondLogin.TwoFactorRequired.Should().BeFalse();
        secondLogin.AccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ClearTrustedDevices_ShouldMakeTheChallengeReturnOnTheNextLogin()
    {
        var (email, password) = await EnableTwoFactorAsync("tfa9", "tfa9@example.com");
        var login = await LoginAsync(email, password);
        var code = ExtractCode(LastEmailTo(email)!);

        var verify = await Client.PostAsJsonAsync(
            "/api/auth/2fa/verify",
            new { challengeToken = login.ChallengeToken, code, rememberDevice = true }
        );
        var verified = (await verify.Content.ReadFromJsonAsync<LoginResponse>())!;
        AuthorizeAs(verified.AccessToken!);

        var clear = await Client.DeleteAsync("/api/auth/2fa/trusted-devices");
        clear.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var secondLogin = await LoginAsync(email, password);
        secondLogin.TwoFactorRequired.Should().BeTrue();
    }

    [Fact]
    public async Task Disable2fa_ShouldReturn401_WithWrongPassword()
    {
        var (email, password) = await EnableTwoFactorAsync("tfa10", "tfa10@example.com");
        var accessToken = await CompleteChallengeAsync(email, password);
        AuthorizeAs(accessToken);

        var response = await Client.PostAsJsonAsync(
            "/api/auth/2fa/disable",
            new { password = "WrongPassword123!" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Disable2fa_ShouldTurnOff2fa_AndClearTrustedDevices_WithCorrectPassword()
    {
        var (email, password) = await EnableTwoFactorAsync("tfa11", "tfa11@example.com");
        var accessToken = await CompleteChallengeAsync(email, password);
        AuthorizeAs(accessToken);

        var response = await Client.PostAsJsonAsync("/api/auth/2fa/disable", new { password });
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var login = await LoginAsync(email, password);
        login.TwoFactorRequired.Should().BeFalse();
        login.AccessToken.Should().NotBeNullOrEmpty();
    }

    // --- Helpers ---

    private HarmonyWebApplicationFactory.CapturedEmail? LastEmailTo(string email) =>
        Factory
            .Services.GetRequiredService<HarmonyWebApplicationFactory.CapturingEmailSender>()
            .Sent.LastOrDefault(e => e.To == email);

    private int SentCount(string email) =>
        Factory
            .Services.GetRequiredService<HarmonyWebApplicationFactory.CapturingEmailSender>()
            .Sent.Count(e => e.To == email);

    /// <summary>Pulls the 6-digit code out of a captured 2FA email's plain-text body.</summary>
    private static string ExtractCode(HarmonyWebApplicationFactory.CapturedEmail email)
    {
        var match = Regex.Match(email.Text, @"Harmony:\s*(?<code>\d{6})");
        match.Success.Should().BeTrue("the email body should contain the 6-digit code");
        return match.Groups["code"].Value;
    }

    private static bool HasCookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(c => c.StartsWith($"{name}=", StringComparison.Ordinal));

    private void AuthorizeAs(string accessToken) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    private async Task<(string Email, string Password)> SetUpVerifiedUserAsync(string username, string email)
    {
        const string password = "Password123!";
        await RegisterAsync(username, email);
        var (uid, token) = ExtractVerifyLink(LastEmailTo(email)!);
        var confirm = await Client.PostAsJsonAsync("/api/auth/verify-email/confirm", new { userId = uid, token });
        confirm.EnsureSuccessStatusCode();
        return (email, password);
    }

    /// <summary>Registers, verifies the email, enables 2FA end-to-end, and returns (email, password) —
    /// the account is left logged out (no Authorization header) so tests can drive their own login.</summary>
    private async Task<(string Email, string Password)> EnableTwoFactorAsync(string username, string email)
    {
        var (verifiedEmail, password) = await SetUpVerifiedUserAsync(username, email);
        var accessToken = await LoginRawAsync(verifiedEmail, password);
        AuthorizeAs(accessToken);

        var request = await Client.PostAsJsonAsync("/api/auth/2fa/enable/request", new { password });
        request.EnsureSuccessStatusCode();

        var code = ExtractCode(LastEmailTo(verifiedEmail)!);
        var confirm = await Client.PostAsJsonAsync("/api/auth/2fa/enable/confirm", new { code });
        confirm.EnsureSuccessStatusCode();

        Client.DefaultRequestHeaders.Authorization = null;
        return (verifiedEmail, password);
    }

    /// <summary>Logs in (expects a 2FA challenge) and completes it, returning the resulting access
    /// token. Does not set rememberDevice.</summary>
    private async Task<string> CompleteChallengeAsync(string email, string password)
    {
        var login = await LoginAsync(email, password);
        var code = ExtractCode(LastEmailTo(email)!);
        var verify = await Client.PostAsJsonAsync(
            "/api/auth/2fa/verify",
            new { challengeToken = login.ChallengeToken, code, rememberDevice = false }
        );
        verify.EnsureSuccessStatusCode();
        return (await verify.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken!;
    }

    private static (string Uid, string Token) ExtractVerifyLink(HarmonyWebApplicationFactory.CapturedEmail email)
    {
        var match = Regex.Match(email.Text, @"verify-email\?uid=(?<uid>\d+)&token=(?<token>[^\s]+)");
        match.Success.Should().BeTrue("the email body should contain the verification link");
        return (match.Groups["uid"].Value, HttpUtility.UrlDecode(match.Groups["token"].Value));
    }

    private async Task<(string AccessToken, string Email, string Password)> RegisterAsync(
        string username,
        string email
    )
    {
        const string password = "Password123!";
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
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        return (auth.AccessToken, email, password);
    }

    private async Task<string> LoginRawAsync(string identifier, string password)
    {
        var login = await LoginAsync(identifier, password);
        login.AccessToken.Should().NotBeNullOrEmpty("this helper is only for pre-2FA logins");
        return login.AccessToken!;
    }

    private async Task<LoginResponse> LoginAsync(string identifier, string password)
    {
        var response = await Client.PostAsJsonAsync("/api/auth/login", new { identifier, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private record AuthResponse(string AccessToken, AuthUser User);

    private record AuthUser(long Id, string Username, string Email);

    private record LoginResponse(
        string? AccessToken,
        AuthUser? User,
        bool TwoFactorRequired,
        string? ChallengeToken
    );
}
