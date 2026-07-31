using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Web;
using FluentAssertions;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Interfaces.Services;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Harmony.IntegrationTests.Auth;

public class CredentialChangeTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public CredentialChangeTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    // --- Change password ---

    [Fact]
    public async Task ChangePassword_ShouldChangeThePassword_SoOldFailsAndNewWorks_AndRevokesOtherSessions()
    {
        const string newPassword = "NewPassword456!";
        var (email, oldPassword) = await RegisterAndLoginAsync(
            "credchange1",
            $"credchange1{Guid.NewGuid():N}@example.com"
        );

        var response = await Client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = oldPassword, newPassword }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        body.AccessToken.Should().NotBeNullOrEmpty();

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

        // Same client, same (now-revoked) refresh_token cookie from the ORIGINAL login —
        // change-password must have rotated it, so replaying the stale cookie 401s.
        // (The response above already set a fresh cookie via the CookieContainer, so instead
        // assert the acting session's own new access token still authorizes a call.)
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            body.AccessToken
        );
        var me = await Client.GetAsync("/api/users/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        Client.DefaultRequestHeaders.Authorization = null;
    }

    [Fact]
    public async Task ChangePassword_ShouldReturn401_ForTheWrongCurrentPassword()
    {
        await RegisterAndLoginAsync("credchange2", $"credchange2{Guid.NewGuid():N}@example.com");

        var response = await Client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = "TotallyWrongPassword!", newPassword = "NewPassword456!" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChangePassword_ShouldReturn400_ForAPasswordlessGoogleOnlyAccount()
    {
        var email = $"credchangegoogle1{Guid.NewGuid():N}@example.com";
        await LoginWithGoogleAsync(email);

        var response = await Client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = "Whatever123!", newPassword = "NewPassword456!" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChangePassword_ShouldRequireCode_AndNotChangeAnything_When2faEnabled()
    {
        var (email, password) = await EnableTwoFactorAsync(
            "credchange13",
            $"credchange13{Guid.NewGuid():N}@example.com"
        );
        AuthorizeAs(await CompleteChallengeAsync(email, password));
        const string newPassword = "NewPassword456!";

        var response = await Client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = password, newPassword }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<ChangePasswordResponse>())!;
        body.RequiresCode.Should().BeTrue();
        body.AccessToken.Should().BeNull();

        // The password must be untouched — the old one still logs in.
        Client.DefaultRequestHeaders.Authorization = null;
        var oldLogin = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { identifier = email, password }
        );
        oldLogin.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ChangePassword_ShouldApply_WithAValidStepUpCode()
    {
        var (email, password) = await EnableTwoFactorAsync(
            "credchange14",
            $"credchange14{Guid.NewGuid():N}@example.com"
        );
        AuthorizeAs(await CompleteChallengeAsync(email, password));
        const string newPassword = "NewPassword456!";

        await Client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = password, newPassword }
        );
        var code = ExtractCode(LastEmailTo(email)!);

        var response = await Client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = password, newPassword, code }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<ChangePasswordResponse>())!;
        body.RequiresCode.Should().BeFalse();
        body.AccessToken.Should().NotBeNullOrEmpty();

        Client.DefaultRequestHeaders.Authorization = null;
        var newLogin = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { identifier = email, password = newPassword }
        );
        newLogin.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ChangePassword_ShouldReturn400_WithAWrongStepUpCode()
    {
        var (email, password) = await EnableTwoFactorAsync(
            "credchange15",
            $"credchange15{Guid.NewGuid():N}@example.com"
        );
        AuthorizeAs(await CompleteChallengeAsync(email, password));
        const string newPassword = "NewPassword456!";

        await Client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = password, newPassword }
        );

        var response = await Client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = password, newPassword, code = "000000" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- Set password ---

    [Fact]
    public async Task SetPassword_ShouldLetAGoogleOnlyAccount_ThenLogInWithAPassword()
    {
        var email = $"credchangegoogle2{Guid.NewGuid():N}@example.com";
        await LoginWithGoogleAsync(email);
        const string newPassword = "BrandNewPassword456!";

        var response = await Client.PostAsJsonAsync("/api/auth/set-password", new { newPassword });
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        Client.DefaultRequestHeaders.Authorization = null;
        var login = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { identifier = email, password = newPassword }
        );
        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SetPassword_ShouldReturn400_WhenAPasswordAlreadyExists()
    {
        await RegisterAndLoginAsync("credchange3", $"credchange3{Guid.NewGuid():N}@example.com");

        var response = await Client.PostAsJsonAsync(
            "/api/auth/set-password",
            new { newPassword = "AnotherPassword456!" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- Change email ---

    [Fact]
    public async Task ChangeEmail_ShouldEmailTheNewAddress_AndConfirmSwitchesLogin()
    {
        var (oldEmail, password) = await RegisterAndLoginAsync(
            "credchange4",
            $"credchange4{Guid.NewGuid():N}@example.com"
        );
        var newEmail = $"credchange4new{Guid.NewGuid():N}@example.com";

        var request = await Client.PostAsJsonAsync(
            "/api/auth/change-email/request",
            new { password, newEmail }
        );
        request.StatusCode.Should().Be(HttpStatusCode.OK);
        var requestBody = await request.Content.ReadFromJsonAsync<ChangeEmailRequestResponse>();
        requestBody!.RequiresCode.Should().BeFalse();

        var (uid, email, token) = ExtractChangeEmailLink(LastEmailTo(newEmail)!);
        email.Should().Be(newEmail);

        var confirm = await Client.PostAsJsonAsync(
            "/api/auth/change-email/confirm",
            new { userId = uid, email, token }
        );
        confirm.StatusCode.Should().Be(HttpStatusCode.NoContent);

        Client.DefaultRequestHeaders.Authorization = null;

        var newLogin = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { identifier = newEmail, password }
        );
        newLogin.StatusCode.Should().Be(HttpStatusCode.OK);

        var oldLogin = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { identifier = oldEmail, password }
        );
        oldLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChangeEmailRequest_ShouldReturn401_ForTheWrongPassword()
    {
        await RegisterAndLoginAsync("credchange5", $"credchange5{Guid.NewGuid():N}@example.com");

        var response = await Client.PostAsJsonAsync(
            "/api/auth/change-email/request",
            new { password = "TotallyWrongPassword!", newEmail = $"x{Guid.NewGuid():N}@example.com" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChangeEmailRequest_ShouldReject_WhenTheNewEmailIsAlreadyInUse()
    {
        var (_, password) = await RegisterAndLoginAsync(
            "credchange6",
            $"credchange6{Guid.NewGuid():N}@example.com"
        );
        var takenEmail = $"credchange6taken{Guid.NewGuid():N}@example.com";
        await RegisterOnlyAsync("credchange6b", takenEmail);

        var response = await Client.PostAsJsonAsync(
            "/api/auth/change-email/request",
            new { password, newEmail = takenEmail }
        );

        // ConflictException("Email already in use.") — the GlobalExceptionHandler maps it to 409
        // (same mapping RegisterAsync uses for a duplicate email/username).
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ConfirmEmailChange_ShouldReturn400_ForATamperedToken()
    {
        var (_, password) = await RegisterAndLoginAsync(
            "credchange7",
            $"credchange7{Guid.NewGuid():N}@example.com"
        );
        var newEmail = $"credchange7new{Guid.NewGuid():N}@example.com";

        await Client.PostAsJsonAsync("/api/auth/change-email/request", new { password, newEmail });
        var (uid, email, _) = ExtractChangeEmailLink(LastEmailTo(newEmail)!);

        var response = await Client.PostAsJsonAsync(
            "/api/auth/change-email/confirm",
            new { userId = uid, email, token = "not-a-real-token" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChangeEmailRequest_ShouldDropTheSecondSend_WithinTheCooldown()
    {
        var (_, password) = await RegisterAndLoginAsync(
            "credchange8",
            $"credchange8{Guid.NewGuid():N}@example.com"
        );
        var newEmail1 = $"credchange8new1{Guid.NewGuid():N}@example.com";
        var newEmail2 = $"credchange8new2{Guid.NewGuid():N}@example.com";

        var first = await Client.PostAsJsonAsync(
            "/api/auth/change-email/request",
            new { password, newEmail = newEmail1 }
        );
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        LastEmailTo(newEmail1).Should().NotBeNull();

        var beforeSecondSend = SentCount(newEmail2);
        var second = await Client.PostAsJsonAsync(
            "/api/auth/change-email/request",
            new { password, newEmail = newEmail2 }
        );
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        // Still inside the "change-email" cooldown window from the first request — no new send.
        SentCount(newEmail2).Should().Be(beforeSecondSend);
    }

    [Fact]
    public async Task ChangeEmailRequest_ShouldRequireCode_AndNotSendTheLink_When2faEnabled()
    {
        var (email, password) = await EnableTwoFactorAsync(
            "credchange16",
            $"credchange16{Guid.NewGuid():N}@example.com"
        );
        AuthorizeAs(await CompleteChallengeAsync(email, password));
        var newEmail = $"credchange16new{Guid.NewGuid():N}@example.com";

        var response = await Client.PostAsJsonAsync(
            "/api/auth/change-email/request",
            new { password, newEmail }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ChangeEmailRequestResponse>();
        body!.RequiresCode.Should().BeTrue();
        LastEmailTo(newEmail).Should().BeNull();
    }

    [Fact]
    public async Task ChangeEmailRequest_ShouldSendTheLink_WithAValidStepUpCode()
    {
        var (email, password) = await EnableTwoFactorAsync(
            "credchange17",
            $"credchange17{Guid.NewGuid():N}@example.com"
        );
        AuthorizeAs(await CompleteChallengeAsync(email, password));
        var newEmail = $"credchange17new{Guid.NewGuid():N}@example.com";

        await Client.PostAsJsonAsync("/api/auth/change-email/request", new { password, newEmail });
        var code = ExtractCode(LastEmailTo(email)!);

        var response = await Client.PostAsJsonAsync(
            "/api/auth/change-email/request",
            new { password, newEmail, code }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ChangeEmailRequestResponse>();
        body!.RequiresCode.Should().BeFalse();
        LastEmailTo(newEmail).Should().NotBeNull();
    }

    [Fact]
    public async Task ChangeEmailRequest_ShouldReturn400_WithAWrongStepUpCode()
    {
        var (email, password) = await EnableTwoFactorAsync(
            "credchange18",
            $"credchange18{Guid.NewGuid():N}@example.com"
        );
        AuthorizeAs(await CompleteChallengeAsync(email, password));
        var newEmail = $"credchange18new{Guid.NewGuid():N}@example.com";

        await Client.PostAsJsonAsync("/api/auth/change-email/request", new { password, newEmail });

        var response = await Client.PostAsJsonAsync(
            "/api/auth/change-email/request",
            new { password, newEmail, code = "000000" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- Change username ---

    [Fact]
    public async Task ChangeUsername_ShouldRenameTheUser_AndBroadcastLive()
    {
        var (_, password) = await RegisterAndLoginAsync(
            "credchange9",
            $"credchange9{Guid.NewGuid():N}@example.com"
        );
        var newUsername = $"renamed{Guid.NewGuid():N}"[..20];

        var response = await Client.PostAsJsonAsync(
            "/api/auth/change-username",
            new { password, newUsername }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var me = await Client.GetAsync("/api/users/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var meBody = await me.Content.ReadFromJsonAsync<MeResponse>();
        meBody!.Username.Should().Be(newUsername);
    }

    [Fact]
    public async Task ChangeUsername_ShouldReturn409_ForANameAlreadyTaken()
    {
        var takenUsername = $"taken{Guid.NewGuid():N}"[..20];
        await RegisterOnlyAsync(takenUsername, $"credchange10other{Guid.NewGuid():N}@example.com");

        var (_, password) = await RegisterAndLoginAsync(
            "credchange10",
            $"credchange10{Guid.NewGuid():N}@example.com"
        );

        var response = await Client.PostAsJsonAsync(
            "/api/auth/change-username",
            new { password, newUsername = takenUsername }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ChangeUsername_ShouldReturn401_ForTheWrongPassword()
    {
        await RegisterAndLoginAsync("credchange11", $"credchange11{Guid.NewGuid():N}@example.com");

        var response = await Client.PostAsJsonAsync(
            "/api/auth/change-username",
            new { password = "TotallyWrongPassword!", newUsername = $"nope{Guid.NewGuid():N}"[..20] }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateMe_ShouldNoLongerRenameTheUser_EvenIfAUsernameFieldIsSent()
    {
        var (_, password) = await RegisterAndLoginAsync(
            "credchange12",
            $"credchange12{Guid.NewGuid():N}@example.com"
        );
        _ = password;

        // UpdateUserRequest no longer has a Username property (Stage E, D14) — an extra
        // "username" field in the JSON body must be silently ignored, not applied.
        var response = await Client.PatchAsJsonAsync(
            "/api/users/me",
            new { username = "shouldnotstick", bio = "a fresh bio" }
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await Client.GetAsync("/api/users/me");
        var meBody = await me.Content.ReadFromJsonAsync<MeResponse>();
        meBody!.Username.Should().Be("credchange12");
        meBody.Bio.Should().Be("a fresh bio");
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

    private static (string Uid, string Email, string Token) ExtractChangeEmailLink(
        HarmonyWebApplicationFactory.CapturedEmail email
    )
    {
        var match = Regex.Match(
            email.Text,
            @"confirm-email-change\?uid=(?<uid>\d+)&email=(?<email>[^&\s]+)&token=(?<token>[^\s]+)"
        );
        match.Success.Should().BeTrue("the email body should contain the change-email confirmation link");
        return (
            match.Groups["uid"].Value,
            HttpUtility.UrlDecode(match.Groups["email"].Value),
            HttpUtility.UrlDecode(match.Groups["token"].Value)
        );
    }

    private async Task RegisterOnlyAsync(string username, string email)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, email, password = "Password123!" }
        );
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Registers a fresh password-based account and leaves Client authenticated (Bearer
    /// header set) with that user's access token. Returns (email, password).</summary>
    private async Task<(string Email, string Password)> RegisterAndLoginAsync(string username, string email)
    {
        const string password = "Password123!";
        var register = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, email, password }
        );
        register.EnsureSuccessStatusCode();
        var body = (await register.Content.ReadFromJsonAsync<AuthResponse>())!;
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            body.AccessToken
        );
        return (email, password);
    }

    /// <summary>Signs in via the fake Google verifier for a brand-new verified identity, leaving
    /// Client authenticated with that (passwordless) account's access token.</summary>
    private async Task LoginWithGoogleAsync(string email)
    {
        var idToken = Factory
            .Services.GetRequiredService<HarmonyWebApplicationFactory.FakeGoogleTokenVerifier>()
            .Register(new GoogleUserInfo(Guid.NewGuid().ToString("N"), email, true, null));

        var response = await Client.PostAsJsonAsync("/api/auth/google", new { idToken });
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            body.AccessToken
        );
    }

    /// <summary>Pulls the 6-digit code out of a captured 2FA/step-up email's plain-text body — the
    /// three code templates (login 2FA, change-password step-up, change-email step-up) all end
    /// their lead-in sentence with ": {code}", so a generic colon-then-6-digits match covers all of
    /// them without depending on the exact wording.</summary>
    private static string ExtractCode(HarmonyWebApplicationFactory.CapturedEmail email)
    {
        var match = Regex.Match(email.Text, @":\s*(?<code>\d{6})\b");
        match.Success.Should().BeTrue("the email body should contain the 6-digit code");
        return match.Groups["code"].Value;
    }

    private static (string Uid, string Token) ExtractVerifyLink(HarmonyWebApplicationFactory.CapturedEmail email)
    {
        var match = Regex.Match(email.Text, @"verify-email\?uid=(?<uid>\d+)&token=(?<token>[^\s]+)");
        match.Success.Should().BeTrue("the email body should contain the verification link");
        return (match.Groups["uid"].Value, HttpUtility.UrlDecode(match.Groups["token"].Value));
    }

    private void AuthorizeAs(string accessToken) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    private async Task<(string Email, string Password)> SetUpVerifiedUserAsync(string username, string email)
    {
        const string password = "Password123!";
        await RegisterOnlyAsync(username, email);
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

    private record MeResponse(long Id, string Username, string? Bio);

    private record LoginResponse(
        string? AccessToken,
        AuthUser? User,
        bool TwoFactorRequired,
        string? ChallengeToken
    );
}
