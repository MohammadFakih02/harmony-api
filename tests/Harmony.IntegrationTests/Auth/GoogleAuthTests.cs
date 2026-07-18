using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Web;
using FluentAssertions;
using Harmony.Application.Interfaces.Services;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Harmony.IntegrationTests.Auth;

public class GoogleAuthTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public GoogleAuthTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    [Fact]
    public async Task GoogleLogin_ShouldCreateANewAccount_ForABrandNewVerifiedIdentity()
    {
        var email = $"googlenew{Guid.NewGuid():N}@example.com";
        var idToken = RegisterGoogleIdentity(email, emailVerified: true);

        var response = await Client.PostAsJsonAsync("/api/auth/google", new { idToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        body.TwoFactorRequired.Should().BeFalse();
        body.AccessToken.Should().NotBeNullOrEmpty();
        body.User!.Email.Should().Be(email);
        body.User.EmailVerified.Should().BeTrue();
    }

    [Fact]
    public async Task GoogleLogin_ShouldAutoLink_WhenEmailMatchesAnExistingVerifiedAccount()
    {
        var (email, password) = await RegisterAsync("googlelink1", $"googlelink1{Guid.NewGuid():N}@example.com");
        var registerBody = await LastRegisterBody();
        var idToken = RegisterGoogleIdentity(email, emailVerified: true);

        var response = await Client.PostAsJsonAsync("/api/auth/google", new { idToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        body.User!.Id.Should().Be(registerBody.User.Id);

        // The original password must still work — linking must not disturb it.
        var passwordLogin = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { identifier = email, password }
        );
        passwordLogin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GoogleLogin_ShouldReject_WhenMatchingEmailIsNotVerifiedByGoogle()
    {
        var (email, password) = await RegisterAsync("googlelink2", $"googlelink2{Guid.NewGuid():N}@example.com");
        var idToken = RegisterGoogleIdentity(email, emailVerified: false);

        var response = await Client.PostAsJsonAsync("/api/auth/google", new { idToken });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Account untouched — the original password still works.
        var passwordLogin = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { identifier = email, password }
        );
        passwordLogin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GoogleLogin_ShouldReject_ForABrandNewUnverifiedEmail_AndCreateNoAccount()
    {
        var email = $"googleunverified{Guid.NewGuid():N}@example.com";
        var idToken = RegisterGoogleIdentity(email, emailVerified: false);

        var response = await Client.PostAsJsonAsync("/api/auth/google", new { idToken });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // No account was created — a normal registration with that same email must still succeed.
        var register = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new { username = $"neverexisted{Guid.NewGuid():N}"[..20], email, password = "Password123!" }
        );
        register.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GoogleLogin_ShouldResolveTheSameAccount_OnASecondSignIn()
    {
        var email = $"googlerepeat{Guid.NewGuid():N}@example.com";
        var subject = Guid.NewGuid().ToString("N");
        var firstToken = RegisterGoogleIdentity(email, emailVerified: true, subject: subject);

        var first = await Client.PostAsJsonAsync("/api/auth/google", new { idToken = firstToken });
        var firstBody = (await first.Content.ReadFromJsonAsync<LoginResponse>())!;

        var secondToken = RegisterGoogleIdentity(email, emailVerified: true, subject: subject);
        var second = await Client.PostAsJsonAsync("/api/auth/google", new { idToken = secondToken });
        var secondBody = (await second.Content.ReadFromJsonAsync<LoginResponse>())!;

        secondBody.User!.Id.Should().Be(firstBody.User!.Id);
    }

    [Fact]
    public async Task GoogleLogin_ShouldSucceedWithoutAChallenge_ForA2faEnabledAccount()
    {
        var email = $"google2fa{Guid.NewGuid():N}@example.com";
        await EnableTwoFactorAsync("google2fauser", email);
        var idToken = RegisterGoogleIdentity(email, emailVerified: true);

        var response = await Client.PostAsJsonAsync("/api/auth/google", new { idToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        body.TwoFactorRequired.Should().BeFalse();
        body.AccessToken.Should().NotBeNullOrEmpty();
    }

    // --- Helpers ---

    private string RegisterGoogleIdentity(string email, bool emailVerified, string? subject = null, string? name = null) =>
        Factory
            .Services.GetRequiredService<HarmonyWebApplicationFactory.FakeGoogleTokenVerifier>()
            .Register(new GoogleUserInfo(subject ?? Guid.NewGuid().ToString("N"), email, emailVerified, name));

    private HarmonyWebApplicationFactory.CapturedEmail? LastEmailTo(string email) =>
        Factory
            .Services.GetRequiredService<HarmonyWebApplicationFactory.CapturingEmailSender>()
            .Sent.LastOrDefault(e => e.To == email);

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

    private AuthResponse? _lastRegisterBody;

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
        _lastRegisterBody = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return (email, password);
    }

    private Task<AuthResponse> LastRegisterBody() => Task.FromResult(_lastRegisterBody!);

    /// <summary>Registers, verifies the email, and enables 2FA via the emailed setup code. Leaves
    /// the account logged out (no Authorization header) with TwoFactorEnabled = true.</summary>
    private async Task<(string Email, string Password)> EnableTwoFactorAsync(string username, string email)
    {
        var (_, password) = await RegisterAsync(username, email);
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
        return (email, password);
    }

    private record AuthResponse(string AccessToken, AuthUser User);

    private record LoginResponse(
        string? AccessToken,
        AuthUser? User,
        bool TwoFactorRequired,
        string? ChallengeToken
    );

    private record AuthUser(long Id, string Username, string Email, bool EmailVerified, bool TwoFactorEnabled);
}
