using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.Notifications;

/// <summary>
/// Notification-preferences CRUD: defaults read all-true, a PATCH persists, and a partial
/// PATCH leaves the unspecified flags untouched.
/// </summary>
public class NotificationPreferenceTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public NotificationPreferenceTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    private async Task<string> RegisterAsync(string username, string email)
    {
        var resp = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                username,
                email,
                password = "Password123!",
            }
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            body!.AccessToken
        );
        return body.AccessToken;
    }

    [Fact]
    public async Task GetPreferences_ReturnsAllEnabled_ByDefault()
    {
        await RegisterAsync("np_a1", "np_a1@test.com");

        var prefs = await Client.GetFromJsonAsync<PrefDto>("/api/notifications/preferences");

        prefs!.MentionsEnabled.Should().BeTrue();
        prefs.RepliesEnabled.Should().BeTrue();
        prefs.FriendRequests.Should().BeTrue();
        prefs.GuildInvites.Should().BeTrue();
        prefs.PushEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task PatchPreferences_PersistsChange()
    {
        await RegisterAsync("np_a2", "np_a2@test.com");

        var patched = await (
            await Client.PatchAsJsonAsync(
                "/api/notifications/preferences",
                new { mentionsEnabled = false }
            )
        ).Content.ReadFromJsonAsync<PrefDto>();
        patched!.MentionsEnabled.Should().BeFalse();

        var reread = await Client.GetFromJsonAsync<PrefDto>("/api/notifications/preferences");
        reread!.MentionsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task PatchPreferences_IsPartial_LeavesOtherFlagsUntouched()
    {
        await RegisterAsync("np_a3", "np_a3@test.com");

        await Client.PatchAsJsonAsync(
            "/api/notifications/preferences",
            new { pushEnabled = false }
        );

        var prefs = await Client.GetFromJsonAsync<PrefDto>("/api/notifications/preferences");
        prefs!.PushEnabled.Should().BeFalse();
        prefs.MentionsEnabled.Should().BeTrue(); // untouched
        prefs.FriendRequests.Should().BeTrue(); // untouched
    }

    private record AuthResponse(string AccessToken);

    private record PrefDto(
        bool MentionsEnabled,
        bool RepliesEnabled,
        bool FriendRequests,
        bool GuildInvites,
        bool PushEnabled
    );
}
