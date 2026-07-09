using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.Files;

/// <summary>
/// Group-DM icon pipeline against the real Postgres + MinIO stack: participant-gated
/// presign → direct PUT → confirm sets Channels.IconKey (surfaced on the DM list), the
/// anonymous public-serve endpoint 302s to it, a non-participant is rejected at presign,
/// a 1:1 DM can't have an icon, and remove clears + retires it.
/// </summary>
public class GroupDmIconTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public GroupDmIconTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    // A small but genuinely valid PNG (2x3) — same fixture as FileUploadTests.
    private static readonly byte[] SmallPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAYAAAC56t6BAAAACXBIWXMAAA7EAAAOxAGVKw4bAAAADUlEQVR4nGNhgAJMBgAA8wANhHf77wAAAABJRU5ErkJggg=="
    );

    private async Task<(string Token, long UserId)> RegisterAsync(string username, string email)
    {
        var resp = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, email, password = "Password123!" }
        );
        resp.EnsureSuccessStatusCode();
        var auth = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (auth!.AccessToken, auth.User.Id);
    }

    private void Auth(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<long> CreateGroupAsync(string name, params long[] userIds)
    {
        var resp = await Client.PostAsJsonAsync("/api/dm/group", new { name, userIds });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<DmDto>())!.ChannelId;
    }

    private async Task<string> UploadIconAsync(long channelId, byte[] bytes)
    {
        var presignResp = await Client.PostAsJsonAsync(
            $"/api/dm/{channelId}/icon/presign",
            new { filename = "pic.png", contentType = "image/png", sizeBytes = bytes.Length }
        );
        presignResp.EnsureSuccessStatusCode();
        var presign = await presignResp.Content.ReadFromJsonAsync<PresignResponse>();

        using var http = new HttpClient();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        (await http.PutAsync(presign!.UploadUrl, content)).EnsureSuccessStatusCode();

        var confirmResp = await Client.PostAsync(
            $"/api/dm/{channelId}/icon/{presign.FileId}/confirm",
            null
        );
        confirmResp.EnsureSuccessStatusCode();
        return (await confirmResp.Content.ReadFromJsonAsync<AssetResponse>())!.Key;
    }

    [Fact]
    public async Task IconUpload_SetsChannelIconKey_AndServesPublicly()
    {
        var (tokenA, _) = await RegisterAsync("gdi_a1", "gdi_a1@test.com");
        var (_, idB) = await RegisterAsync("gdi_b1", "gdi_b1@test.com");
        var (_, idC) = await RegisterAsync("gdi_c1", "gdi_c1@test.com");

        Auth(tokenA);
        var channelId = await CreateGroupAsync("Icon Group", idB, idC);

        var key = await UploadIconAsync(channelId, SmallPng);
        key.Should().StartWith($"channel-icons/{channelId}/");

        // The icon key surfaces on the DM list for participants.
        var dms = await Client.GetFromJsonAsync<List<DmDto>>("/api/dm");
        dms.Should().ContainSingle(d => d.ChannelId == channelId && d.IconKey == key);

        using var anon = Factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            }
        );
        var serve = await anon.GetAsync($"/api/files/public/{key}");
        serve.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task IconRemove_ClearsTheKey_AndRetiresTheObject()
    {
        var (tokenA, _) = await RegisterAsync("gdi_a2", "gdi_a2@test.com");
        var (_, idB) = await RegisterAsync("gdi_b2", "gdi_b2@test.com");
        var (_, idC) = await RegisterAsync("gdi_c2", "gdi_c2@test.com");

        Auth(tokenA);
        var channelId = await CreateGroupAsync("Removable", idB, idC);
        var key = await UploadIconAsync(channelId, SmallPng);

        (await Client.DeleteAsync($"/api/dm/{channelId}/icon"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var dms = await Client.GetFromJsonAsync<List<DmDto>>("/api/dm");
        dms.Should().ContainSingle(d => d.ChannelId == channelId && d.IconKey == null);

        // The retired asset no longer serves.
        (await Client.GetAsync($"/api/files/public/{key}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NonParticipant_CannotPresignAGroupIcon()
    {
        var (tokenA, _) = await RegisterAsync("gdi_a3", "gdi_a3@test.com");
        var (_, idB) = await RegisterAsync("gdi_b3", "gdi_b3@test.com");
        var (_, idC) = await RegisterAsync("gdi_c3", "gdi_c3@test.com");
        var (tokenD, _) = await RegisterAsync("gdi_d3", "gdi_d3@test.com");

        Auth(tokenA);
        var channelId = await CreateGroupAsync("Gated", idB, idC);

        Auth(tokenD);
        var presign = await Client.PostAsJsonAsync(
            $"/api/dm/{channelId}/icon/presign",
            new { filename = "pic.png", contentType = "image/png", sizeBytes = SmallPng.Length }
        );
        presign.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OneToOneDm_CannotHaveAnIcon()
    {
        var (tokenA, _) = await RegisterAsync("gdi_a4", "gdi_a4@test.com");
        var (_, idB) = await RegisterAsync("gdi_b4", "gdi_b4@test.com");

        Auth(tokenA);
        var open = await Client.PostAsJsonAsync("/api/dm", new { targetUserId = idB });
        open.EnsureSuccessStatusCode();
        var dm = await open.Content.ReadFromJsonAsync<DmDto>();

        var presign = await Client.PostAsJsonAsync(
            $"/api/dm/{dm!.ChannelId}/icon/presign",
            new { filename = "pic.png", contentType = "image/png", sizeBytes = SmallPng.Length }
        );
        presign.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record DmDto(long ChannelId, bool IsGroup, string? Name, string? IconKey);

    private record PresignResponse(long FileId, string UploadUrl, string ObjectKey, long ExpiresAt);

    private record AssetResponse(string Key);
}
