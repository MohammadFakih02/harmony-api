using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Web;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Harmony.IntegrationTests.Auth;

public class PasswordResetTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public PasswordResetTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    [Fact]
    public async Task ForgotPassword_ShouldReturn204_AndSendNothing_ForAnUnknownEmail()
    {
        var before = SentCount("nobody@example.com");

        var response = await Client.PostAsJsonAsync(
            "/api/auth/forgot-password",
            new { email = "nobody@example.com" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        SentCount("nobody@example.com").Should().Be(before);
    }

    [Fact]
    public async Task ForgotPassword_ShouldEmailAResetLink_ForAKnownEmail()
    {
        var (email, _) = await RegisterAsync("resetuser1", "reset1@example.com");

        var response = await Client.PostAsJsonAsync("/api/auth/forgot-password", new { email });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var (uid, token) = ExtractResetLink(LastEmailTo(email)!);
        uid.Should().NotBeNullOrEmpty();
        token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ResetPassword_ShouldChangeThePassword_SoOldFailsAndNewWorks()
    {
        var (email, oldPassword) = await RegisterAsync("resetuser2", "reset2@example.com");
        const string newPassword = "NewPassword456!";

        await Client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        var (uid, token) = ExtractResetLink(LastEmailTo(email)!);

        var reset = await Client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new { userId = uid, token, newPassword }
        );
        reset.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var oldLogin = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { identifier = email, password = oldPassword }
        );
        oldLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var newLogin = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { identifier = email, password = newPassword }
        );
        newLogin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResetPassword_ShouldRevokeExistingSessions_SoTheOldRefreshCookie401s()
    {
        // RegisterAsync's Client already carries the refresh_token cookie from registration.
        var (email, _) = await RegisterAsync("resetuser3", "reset3@example.com");

        await Client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        var (uid, token) = ExtractResetLink(LastEmailTo(email)!);

        var reset = await Client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new { userId = uid, token, newPassword = "NewPassword456!" }
        );
        reset.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Same client, same (now-revoked) refresh_token cookie — reset-password never touches it.
        var refresh = await Client.PostAsJsonAsync("/api/auth/refresh", new { });
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResetPassword_ShouldReturn400_WhenTheTokenIsReused()
    {
        var (email, _) = await RegisterAsync("resetuser4", "reset4@example.com");

        await Client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        var (uid, token) = ExtractResetLink(LastEmailTo(email)!);

        var first = await Client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new { userId = uid, token, newPassword = "NewPassword456!" }
        );
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Identity bumps the security stamp on a successful reset, invalidating the token it just
        // consumed — so replaying it must fail even though it's syntactically well-formed.
        var second = await Client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new { userId = uid, token, newPassword = "AnotherPassword789!" }
        );
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResetPassword_ShouldReturn400_WithAnUnknownUserId()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new { userId = "999999999999999999", token = "not-a-real-token", newPassword = "NewPassword456!" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResetPassword_ShouldClearTrustedDevices_SoLoginChallengesAgain()
    {
        var (email, password) = await EnableTwoFactorWithRememberedDeviceAsync(
            "resetuser5",
            "reset5@example.com"
        );

        await Client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        var (uid, token) = ExtractResetLink(LastEmailTo(email)!);
        const string newPassword = "NewPassword456!";

        var reset = await Client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new { userId = uid, token, newPassword }
        );
        reset.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Same client (still carries the "trusted_device" cookie) — it must no longer be honored.
        var login = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { identifier = email, password = newPassword }
        );
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        body.TwoFactorRequired.Should().BeTrue();
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

    private static (string Uid, string Token) ExtractResetLink(
        HarmonyWebApplicationFactory.CapturedEmail email
    )
    {
        var match = Regex.Match(email.Text, @"reset-password\?uid=(?<uid>\d+)&token=(?<token>[^\s]+)");
        match.Success.Should().BeTrue("the email body should contain the reset link");
        return (match.Groups["uid"].Value, HttpUtility.UrlDecode(match.Groups["token"].Value));
    }

    private static (string Uid, string Token) ExtractVerifyLink(
        HarmonyWebApplicationFactory.CapturedEmail email
    )
    {
        var match = Regex.Match(email.Text, @"verify-email\?uid=(?<uid>\d+)&token=(?<token>[^\s]+)");
        match.Success.Should().BeTrue("the email body should contain the verification link");
        return (match.Groups["uid"].Value, HttpUtility.UrlDecode(match.Groups["token"].Value));
    }

    /// <summary>Pulls the 6-digit code out of a captured 2FA email's plain-text body.</summary>
    private static string ExtractCode(HarmonyWebApplicationFactory.CapturedEmail email)
    {
        var match = Regex.Match(email.Text, @"Harmony:\s*(?<code>\d{6})");
        match.Success.Should().BeTrue("the email body should contain the 6-digit code");
        return match.Groups["code"].Value;
    }

    private async Task<(string Email, string Password)> RegisterAsync(string username, string email)
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
        return (email, password);
    }

    /// <summary>Registers, verifies the email, enables 2FA, logs in, and completes the challenge
    /// with rememberDevice — leaving Client holding a valid "trusted_device" cookie. Returns
    /// (email, password) with the account left logged out (no Authorization header).</summary>
    private async Task<(string Email, string Password)> EnableTwoFactorWithRememberedDeviceAsync(
        string username,
        string email
    )
    {
        const string password = "Password123!";
        await RegisterAsync(username, email);
        var (uid, verifyToken) = ExtractVerifyLink(LastEmailTo(email)!);
        var confirm = await Client.PostAsJsonAsync(
            "/api/auth/verify-email/confirm",
            new { userId = uid, token = verifyToken }
        );
        confirm.EnsureSuccessStatusCode();

        var login = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { identifier = email, password }
        );
        var loginBody = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            loginBody.AccessToken
        );

        var enableRequest = await Client.PostAsJsonAsync(
            "/api/auth/2fa/enable/request",
            new { password }
        );
        enableRequest.EnsureSuccessStatusCode();
        var setupCode = ExtractCode(LastEmailTo(email)!);
        var enableConfirm = await Client.PostAsJsonAsync(
            "/api/auth/2fa/enable/confirm",
            new { code = setupCode }
        );
        enableConfirm.EnsureSuccessStatusCode();

        Client.DefaultRequestHeaders.Authorization = null;

        var secondLogin = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { identifier = email, password }
        );
        var challenge = (await secondLogin.Content.ReadFromJsonAsync<LoginResponse>())!;
        var code = ExtractCode(LastEmailTo(email)!);
        var verify = await Client.PostAsJsonAsync(
            "/api/auth/2fa/verify",
            new { challengeToken = challenge.ChallengeToken, code, rememberDevice = true }
        );
        verify.EnsureSuccessStatusCode();

        Client.DefaultRequestHeaders.Authorization = null;
        return (email, password);
    }

    private record LoginResponse(
        string? AccessToken,
        AuthUser? User,
        bool TwoFactorRequired,
        string? ChallengeToken
    );

    private record AuthUser(long Id, string Username, string Email);
}
