using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Files;

/// <summary>
/// File-upload pipeline against the real Postgres + Redis + MinIO stack: presign (AttachFiles-gated),
/// a direct PUT to the presigned URL, then confirm (verifies the object landed, finalizes from the
/// store's authoritative size + decoded dimensions). Requires a running MinIO (see CI / docker stack).
/// </summary>
public class FileUploadTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public FileUploadTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    // A small but genuinely valid PNG (2x3) — encoded by ImageSharp so it decodes cleanly.
    private static readonly byte[] SmallPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAYAAAC56t6BAAAACXBIWXMAAA7EAAAOxAGVKw4bAAAADUlEQVR4nGNhgAJMBgAA8wANhHf77wAAAABJRU5ErkJggg=="
    );

    [Fact]
    public async Task PresignUploadConfirm_HappyPath_ConfirmsWithDimensions()
    {
        var ownerToken = await RegisterAsync("fileowner1", "fileowner1@test.com");
        var (guildId, _) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);

        Auth(ownerToken);
        var presignResp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/presign",
            new { filename = "pixel.png", contentType = "image/png", sizeBytes = SmallPng.Length }
        );
        presignResp.EnsureSuccessStatusCode();
        var presign = await presignResp.Content.ReadFromJsonAsync<PresignResponse>();
        presign!.UploadUrl.Should().NotBeNullOrEmpty();

        // Upload straight to MinIO via the presigned URL (a real HTTP PUT, not the test client).
        using var http = new HttpClient();
        var content = new ByteArrayContent(SmallPng);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var putResp = await http.PutAsync(presign.UploadUrl, content);
        putResp.IsSuccessStatusCode.Should().BeTrue("the presigned PUT should be accepted by MinIO");

        Auth(ownerToken);
        var confirmResp = await Client.PostAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/{presign.FileId}/confirm",
            null
        );
        confirmResp.EnsureSuccessStatusCode();
        var file = await confirmResp.Content.ReadFromJsonAsync<FileResponse>();

        file!.IsConfirmed.Should().BeTrue();
        file.Width.Should().Be(2);
        file.Height.Should().Be(3);
        file.SizeBytes.Should().Be(SmallPng.Length);
    }

    [Fact]
    public async Task Presign_WhenAttachFilesDeniedByOverride_IsForbidden()
    {
        var ownerToken = await RegisterAsync("fileowner2", "fileowner2@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var (ownerId, everyoneId) = GuildFacts(guildId);

        var memberToken = await RegisterAsync("filemember2", "filemember2@test.com");
        await JoinAsync(memberToken, invite);

        // Deny AttachFiles for @everyone on this channel.
        Auth(ownerToken);
        var put = await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/overrides/{everyoneId}",
            new { targetType = "role", allowBits = 0L, denyBits = (long)Permission.AttachFiles }
        );
        put.EnsureSuccessStatusCode();

        Auth(memberToken);
        var presignResp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/presign",
            new { filename = "pixel.png", contentType = "image/png", sizeBytes = SmallPng.Length }
        );
        presignResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Confirm_BeforeUpload_IsBadRequest()
    {
        var ownerToken = await RegisterAsync("fileowner3", "fileowner3@test.com");
        var (guildId, _) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);

        Auth(ownerToken);
        var presignResp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/presign",
            new { filename = "pixel.png", contentType = "image/png", sizeBytes = SmallPng.Length }
        );
        presignResp.EnsureSuccessStatusCode();
        var presign = await presignResp.Content.ReadFromJsonAsync<PresignResponse>();

        // No PUT — the object never lands in MinIO, so confirm must reject it.
        var confirmResp = await Client.PostAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/{presign!.FileId}/confirm",
            null
        );
        confirmResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Download_HappyPath_ReturnsUrlThatServesTheBytes()
    {
        var ownerToken = await RegisterAsync("fileowner4", "fileowner4@test.com");
        var (guildId, _) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var fileId = await UploadConfirmedFileAsync(ownerToken, guildId, channelId);

        Auth(ownerToken);
        var resp = await Client.GetAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/{fileId}"
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<FileUrlResponse>();
        body!.Url.Should().NotBeNullOrEmpty();

        // The presigned GET URL should actually serve the bytes we uploaded.
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(body.Url);
        bytes.Length.Should().Be(SmallPng.Length);
    }

    [Fact]
    public async Task Download_WhenViewChannelDeniedByOverride_IsForbidden()
    {
        var ownerToken = await RegisterAsync("fileowner5", "fileowner5@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var (_, everyoneId) = GuildFacts(guildId);
        var fileId = await UploadConfirmedFileAsync(ownerToken, guildId, channelId);

        var memberToken = await RegisterAsync("filemember5", "filemember5@test.com");
        await JoinAsync(memberToken, invite);

        // Deny ViewChannel for @everyone on this channel.
        Auth(ownerToken);
        var put = await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/overrides/{everyoneId}",
            new { targetType = "role", allowBits = 0L, denyBits = (long)Permission.ViewChannel }
        );
        put.EnsureSuccessStatusCode();

        Auth(memberToken);
        var resp = await Client.GetAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/{fileId}"
        );
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Download_UnconfirmedFile_IsNotFound()
    {
        var ownerToken = await RegisterAsync("fileowner6", "fileowner6@test.com");
        var (guildId, _) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);

        // Presign only — never uploaded/confirmed.
        Auth(ownerToken);
        var presignResp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/presign",
            new { filename = "pixel.png", contentType = "image/png", sizeBytes = SmallPng.Length }
        );
        presignResp.EnsureSuccessStatusCode();
        var presign = await presignResp.Content.ReadFromJsonAsync<PresignResponse>();

        var resp = await Client.GetAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/{presign!.FileId}"
        );
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Helpers (mirror ChannelOverrideTests) ----------------------------

    /// <summary>Full presign → PUT → confirm, returning the confirmed file's id.</summary>
    private async Task<long> UploadConfirmedFileAsync(string ownerToken, long guildId, long channelId)
    {
        Auth(ownerToken);
        var presignResp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/presign",
            new { filename = "pixel.png", contentType = "image/png", sizeBytes = SmallPng.Length }
        );
        presignResp.EnsureSuccessStatusCode();
        var presign = await presignResp.Content.ReadFromJsonAsync<PresignResponse>();

        using var http = new HttpClient();
        var content = new ByteArrayContent(SmallPng);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        (await http.PutAsync(presign!.UploadUrl, content)).EnsureSuccessStatusCode();

        Auth(ownerToken);
        var confirmResp = await Client.PostAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/{presign.FileId}/confirm",
            null
        );
        confirmResp.EnsureSuccessStatusCode();
        return presign.FileId;
    }


    private async Task<string> RegisterAsync(string username, string email)
    {
        var resp = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, email, password = "Password123!" }
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.AccessToken;
    }

    private void Auth(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<(long guildId, string inviteCode)> CreateGuildAsync(string token)
    {
        Auth(token);
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "File Guild" });
        resp.EnsureSuccessStatusCode();
        var guild = await resp.Content.ReadFromJsonAsync<GuildResponse>();
        return (guild!.Id, guild.InviteCode!);
    }

    private async Task<long> CreateChannelAsync(string ownerToken, long guildId)
    {
        Auth(ownerToken);
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels",
            new { name = "general", type = "text", position = 0 }
        );
        resp.EnsureSuccessStatusCode();
        var channel = await resp.Content.ReadFromJsonAsync<ChannelResponse>();
        return channel!.Id;
    }

    private async Task JoinAsync(string memberToken, string invite)
    {
        Auth(memberToken);
        var joinResp = await Client.PostAsJsonAsync($"/api/guilds/join/{invite}", new { });
        joinResp.EnsureSuccessStatusCode();
    }

    private (long ownerId, long everyoneRoleId) GuildFacts(long guildId)
    {
        using var scope = Factory.Services.CreateScope();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        var ownerId = guilds.GetByIdAsync(guildId).GetAwaiter().GetResult()!.OwnerId;
        var everyone = roles.GetDefaultRoleAsync(guildId).GetAwaiter().GetResult()!;
        return (ownerId, everyone.Id);
    }

    private record AuthResponse(string AccessToken);

    private record GuildResponse(long Id, string Name, string? InviteCode);

    private record ChannelResponse(long Id, long? GuildId, string Name, string Type);

    private record PresignResponse(long FileId, string UploadUrl, string ObjectKey, long ExpiresAt);

    private record FileUrlResponse(string Url, long ExpiresAt);

    private record FileResponse(
        long Id,
        long ChannelId,
        long GuildId,
        string Filename,
        string ContentType,
        long SizeBytes,
        int? Width,
        int? Height,
        bool IsConfirmed,
        long CreatedAt
    );
}
