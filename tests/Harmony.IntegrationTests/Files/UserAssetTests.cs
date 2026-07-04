using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.Files;

/// <summary>
/// Profile-asset (avatar/banner) pipeline against the real Postgres + MinIO stack: user-scoped
/// presign → direct PUT → confirm sets Users.AvatarKey/BannerKey, the anonymous public-serve
/// endpoint 302s to a presigned GET, replacement retires the previous asset, and junk bytes are
/// rejected. Also covers the banner-colour PATCH added alongside.
/// </summary>
public class UserAssetTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public UserAssetTests(HarmonyWebApplicationFactory factory)
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

    /// <summary>Presign → PUT to MinIO → confirm; returns the asset key now on the profile.</summary>
    private async Task<string> UploadAssetAsync(string kind, byte[] bytes)
    {
        var presignResp = await Client.PostAsJsonAsync(
            $"/api/users/me/{kind}/presign",
            new { filename = "pic.png", contentType = "image/png", sizeBytes = bytes.Length }
        );
        presignResp.EnsureSuccessStatusCode();
        var presign = await presignResp.Content.ReadFromJsonAsync<PresignResponse>();

        using var http = new HttpClient();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        (await http.PutAsync(presign!.UploadUrl, content)).EnsureSuccessStatusCode();

        var confirmResp = await Client.PostAsync(
            $"/api/users/me/{kind}/{presign.FileId}/confirm",
            null
        );
        confirmResp.EnsureSuccessStatusCode();
        return (await confirmResp.Content.ReadFromJsonAsync<AssetResponse>())!.Key;
    }

    [Fact]
    public async Task AvatarUpload_SetsProfileKey_AndServesPublicly()
    {
        var token = await RegisterAsync("asset_av1", "asset_av1@test.com");
        Auth(token);

        var key = await UploadAssetAsync("avatar", SmallPng);
        key.Should().StartWith("avatars/");

        var me = await Client.GetFromJsonAsync<ProfileResponse>("/api/users/me");
        me!.AvatarKey.Should().Be(key);

        // The public serve endpoint is anonymous (img tags carry no JWT) and 302s to MinIO.
        using var anon = Factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            }
        );
        var serve = await anon.GetAsync($"/api/files/public/{key}");
        serve.StatusCode.Should().Be(HttpStatusCode.Redirect);
        serve.Headers.Location.Should().NotBeNull();

        // Following the presigned URL actually returns the bytes.
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(serve.Headers.Location);
        bytes.Should().BeEquivalentTo(SmallPng);
    }

    [Fact]
    public async Task ReplacingAnAvatar_RetiresThePreviousObject()
    {
        var token = await RegisterAsync("asset_av2", "asset_av2@test.com");
        Auth(token);

        var firstKey = await UploadAssetAsync("avatar", SmallPng);
        var secondKey = await UploadAssetAsync("avatar", SmallPng);
        secondKey.Should().NotBe(firstKey);

        var me = await Client.GetFromJsonAsync<ProfileResponse>("/api/users/me");
        me!.AvatarKey.Should().Be(secondKey);

        // The replaced asset no longer serves (row retired).
        (await Client.GetAsync($"/api/files/public/{firstKey}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BannerUpload_And_Remove_ClearTheProfileKey()
    {
        var token = await RegisterAsync("asset_bn1", "asset_bn1@test.com");
        Auth(token);

        var key = await UploadAssetAsync("banner", SmallPng);
        (await Client.GetFromJsonAsync<ProfileResponse>("/api/users/me"))!
            .BannerKey.Should().Be(key);

        (await Client.DeleteAsync("/api/users/me/banner"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Client.GetFromJsonAsync<ProfileResponse>("/api/users/me"))!
            .BannerKey.Should().BeNull();
        (await Client.GetAsync($"/api/files/public/{key}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Confirm_RejectsJunkBytesDeclaredAsAnImage()
    {
        var token = await RegisterAsync("asset_junk", "asset_junk@test.com");
        Auth(token);

        var junk = System.Text.Encoding.ASCII.GetBytes("definitely not a png");
        var presignResp = await Client.PostAsJsonAsync(
            "/api/users/me/avatar/presign",
            new { filename = "junk.png", contentType = "image/png", sizeBytes = junk.Length }
        );
        presignResp.EnsureSuccessStatusCode();
        var presign = await presignResp.Content.ReadFromJsonAsync<PresignResponse>();

        using var http = new HttpClient();
        var content = new ByteArrayContent(junk);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        (await http.PutAsync(presign!.UploadUrl, content)).EnsureSuccessStatusCode();

        var confirmResp = await Client.PostAsync(
            $"/api/users/me/avatar/{presign.FileId}/confirm",
            null
        );
        confirmResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PublicServe_NeverExposesChatAttachments()
    {
        // Even a syntactically plausible attachments/… key must 404 (prefix gate).
        var resp = await Client.GetAsync("/api/files/public/attachments/1/2/3");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BannerColor_PatchPersists_AndValidates()
    {
        var token = await RegisterAsync("asset_col", "asset_col@test.com");
        Auth(token);

        var ok = await Client.PatchAsJsonAsync("/api/users/me", new { bannerColor = "#3BA55C" });
        ok.EnsureSuccessStatusCode();
        (await Client.GetFromJsonAsync<ProfileResponse>("/api/users/me"))!
            .BannerColor.Should().Be("#3ba55c");

        var bad = await Client.PatchAsJsonAsync("/api/users/me", new { bannerColor = "green" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var clear = await Client.PatchAsJsonAsync("/api/users/me", new { bannerColor = "" });
        clear.EnsureSuccessStatusCode();
        (await Client.GetFromJsonAsync<ProfileResponse>("/api/users/me"))!
            .BannerColor.Should().BeNull();
    }

    private record AuthResponse(string AccessToken);

    private record PresignResponse(long FileId, string UploadUrl, string ObjectKey, long ExpiresAt);

    private record AssetResponse(string Key);

    private record ProfileResponse(
        long Id,
        string Username,
        string? AvatarKey,
        string? BannerKey,
        string? BannerColor
    );
}
