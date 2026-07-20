using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using SixLabors.ImageSharp;
using Xunit;

namespace Harmony.IntegrationTests.Files;

/// <summary>
/// Guild-asset (icon/banner) pipeline against the real Postgres + MinIO stack: ManageGuild-gated
/// presign → direct PUT → confirm sets Guilds.IconKey/BannerKey, the anonymous public-serve
/// endpoint 302s to it, and a plain member is rejected at presign.
/// </summary>
public class GuildAssetTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public GuildAssetTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    // A small but genuinely valid PNG (2x3) — same fixture as FileUploadTests.
    private static readonly byte[] SmallPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAYAAAC56t6BAAAACXBIWXMAAA7EAAAOxAGVKw4bAAAADUlEQVR4nGNhgAJMBgAA8wANhHf77wAAAABJRU5ErkJggg=="
    );

    private async Task<string> RegisterAsync(string username, string email)
    {
        var resp = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, email, password = "Password123!" }
        );
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken;
    }

    private void Auth(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<long> CreateGuildAsync(string name)
    {
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<GuildDto>())!.Id;
    }

    private async Task<string> UploadGuildAssetAsync(long guildId, string kind, byte[] bytes)
    {
        var presignResp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/{kind}/presign",
            new { filename = "pic.png", contentType = "image/png", sizeBytes = bytes.Length }
        );
        presignResp.EnsureSuccessStatusCode();
        var presign = await presignResp.Content.ReadFromJsonAsync<PresignResponse>();

        using var http = new HttpClient();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        (await http.PutAsync(presign!.UploadUrl, content)).EnsureSuccessStatusCode();

        var confirmResp = await Client.PostAsync(
            $"/api/guilds/{guildId}/{kind}/{presign.FileId}/confirm",
            null
        );
        confirmResp.EnsureSuccessStatusCode();
        return (await confirmResp.Content.ReadFromJsonAsync<AssetResponse>())!.Key;
    }

    [Fact]
    public async Task IconUpload_SetsGuildIconKey_AndServesPublicly()
    {
        var token = await RegisterAsync("gasset_o1", "gasset_o1@test.com");
        Auth(token);
        var guildId = await CreateGuildAsync("Icon Guild");

        var key = await UploadGuildAssetAsync(guildId, "icon", SmallPng);
        key.Should().StartWith($"guild-icons/{guildId}/");

        var guild = await Client.GetFromJsonAsync<GuildDto>($"/api/guilds/{guildId}");
        guild!.IconKey.Should().Be(key);

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
    public async Task OversizedBanner_IsCappedTo1280OnTheServer()
    {
        var token = await RegisterAsync("gasset_cap", "gasset_cap@test.com");
        Auth(token);
        var guildId = await CreateGuildAsync("Cap Guild");

        byte[] bigPng;
        using (var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(3840, 2160))
        using (var ms = new MemoryStream())
        {
            await img.SaveAsPngAsync(ms);
            bigPng = ms.ToArray();
        }

        var key = await UploadGuildAssetAsync(guildId, "banner", bigPng);

        using var anon = Factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            }
        );
        var serve = await anon.GetAsync($"/api/files/public/{key}");
        serve.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var http = new HttpClient();
        var stored = await http.GetByteArrayAsync(serve.Headers.Location);
        var info = SixLabors.ImageSharp.Image.Identify(stored);
        info.Width.Should().BeLessThanOrEqualTo(1280);
        info.Height.Should().BeLessThanOrEqualTo(1280);
    }

    [Fact]
    public async Task BannerRemove_ClearsTheGuildKey()
    {
        var token = await RegisterAsync("gasset_o2", "gasset_o2@test.com");
        Auth(token);
        var guildId = await CreateGuildAsync("Banner Guild");

        var key = await UploadGuildAssetAsync(guildId, "banner", SmallPng);

        (await Client.DeleteAsync($"/api/guilds/{guildId}/banner"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var guild = await Client.GetFromJsonAsync<GuildDto>($"/api/guilds/{guildId}");
        guild!.BannerKey.Should().BeNull();

        // The retired asset no longer serves.
        (await Client.GetAsync($"/api/files/public/{key}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PlainMember_CannotPresignAGuildIcon()
    {
        var ownerToken = await RegisterAsync("gasset_o3", "gasset_o3@test.com");
        Auth(ownerToken);
        var guildId = await CreateGuildAsync("Gated Guild");
        var code = await CreateInviteCodeAsync(guildId);

        var memberToken = await RegisterAsync("gasset_m3", "gasset_m3@test.com");
        Auth(memberToken);
        (await Client.PostAsync($"/api/invites/{code}/join", null)).EnsureSuccessStatusCode();

        var presign = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/icon/presign",
            new { filename = "pic.png", contentType = "image/png", sizeBytes = SmallPng.Length }
        );
        presign.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private record AuthResponse(string AccessToken);

    private record GuildDto(long Id, string Name, string? IconKey, string? BannerKey);

    private record PresignResponse(long FileId, string UploadUrl, string ObjectKey, long ExpiresAt);

    private record AssetResponse(string Key);
}
