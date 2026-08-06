using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Web;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Harmony.IntegrationTests.Auth;

public class EmailVerificationTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public EmailVerificationTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    [Fact]
    public async Task Register_ShouldSendVerificationEmail()
    {
        await RegisterUserAsync("verifyuser1", "verify1@example.com", "Password123!");

        var sent = LastEmailTo("verify1@example.com");
        sent.Should().NotBeNull();
        sent!.Subject.Should().Contain("Verify your email");
    }

    [Fact]
    public async Task Register_ShouldReturnEmailVerifiedFalse()
    {
        var auth = await RegisterUserAsync("verifyuser2", "verify2@example.com", "Password123!");
        auth.User.EmailVerified.Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmEmail_ShouldReturn204_AndMarkVerified_WithValidLink()
    {
        var auth = await RegisterUserAsync("verifyuser3", "verify3@example.com", "Password123!");
        var (uid, token) = ExtractLink(LastEmailTo("verify3@example.com")!);

        var confirm = await Client.PostAsJsonAsync(
            "/api/auth/verify-email/confirm",
            new { userId = uid, token }
        );
        confirm.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var login = await LoginAsync("verify3@example.com", "Password123!");
        login.User.EmailVerified.Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmEmail_ShouldReturn400_WithInvalidToken()
    {
        var auth = await RegisterUserAsync("verifyuser4", "verify4@example.com", "Password123!");

        var response = await Client.PostAsJsonAsync(
            "/api/auth/verify-email/confirm",
            new { userId = auth.User.Id.ToString(), token = "not-a-real-token" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RequestVerification_ShouldSendAnotherEmail_ThenCooldownBlocksTheNext()
    {
        var auth = await RegisterUserAsync("verifyuser5", "verify5@example.com", "Password123!");
        var sender = Factory.Services.GetRequiredService<HarmonyWebApplicationFactory.CapturingEmailSender>();
        var before = sender.Sent.Count(e => e.To == "verify5@example.com");

        Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var first = await Client.PostAsync("/api/auth/verify-email/request", null);
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        sender.Sent.Count(e => e.To == "verify5@example.com").Should().Be(before + 1);

        var second = await Client.PostAsync("/api/auth/verify-email/request", null);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent); // always 204 — cooldown is a silent no-op
        sender.Sent.Count(e => e.To == "verify5@example.com").Should().Be(before + 1); // no new email
    }

    // --- Helpers ---

    private HarmonyWebApplicationFactory.CapturedEmail? LastEmailTo(string email) =>
        Factory
            .Services.GetRequiredService<HarmonyWebApplicationFactory.CapturingEmailSender>()
            .Sent.LastOrDefault(e => e.To == email);

    /// <summary>Pulls uid/token out of the "verify-email?uid=..&amp;token=.." link in a captured email's
    /// plain-text body (already URL-decoded — same as an Angular ActivatedRoute query-param read).</summary>
    private static (string Uid, string Token) ExtractLink(HarmonyWebApplicationFactory.CapturedEmail email)
    {
        var match = Regex.Match(email.Text, @"verify-email\?uid=(?<uid>\d+)&token=(?<token>[^\s]+)");
        match.Success.Should().BeTrue("the email body should contain the verification link");
        return (match.Groups["uid"].Value, HttpUtility.UrlDecode(match.Groups["token"].Value));
    }

    private async Task<AuthResponse> RegisterUserAsync(string username, string email, string password)
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

    private async Task<AuthResponse> LoginAsync(string identifier, string password)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new { identifier, password }
        );
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private record AuthResponse(string AccessToken, AuthUser User);

    private record AuthUser(
        long Id,
        string Username,
        string Email,
        string? AvatarKey,
        string AccountStatus,
        bool EmailVerified,
        bool TwoFactorEnabled
    );
}
