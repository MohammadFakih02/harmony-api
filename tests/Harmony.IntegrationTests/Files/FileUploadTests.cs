using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
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

    // A minimal PDF — only the leading "%PDF" matters to the magic-byte sniff.
    private static readonly byte[] SmallPdf =
        System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF");

    [Fact]
    public async Task PresignUploadConfirm_NonImage_ConfirmsWithNullDimensions()
    {
        var ownerToken = await RegisterAsync("filepdf1", "filepdf1@test.com");
        var (guildId, _) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);

        Auth(ownerToken);
        var presignResp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/presign",
            new { filename = "doc.pdf", contentType = "application/pdf", sizeBytes = SmallPdf.Length }
        );
        presignResp.EnsureSuccessStatusCode();
        var presign = await presignResp.Content.ReadFromJsonAsync<PresignResponse>();

        using var http = new HttpClient();
        var content = new ByteArrayContent(SmallPdf);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        (await http.PutAsync(presign!.UploadUrl, content)).EnsureSuccessStatusCode();

        Auth(ownerToken);
        var confirmResp = await Client.PostAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/{presign.FileId}/confirm",
            null
        );
        confirmResp.EnsureSuccessStatusCode();
        var file = await confirmResp.Content.ReadFromJsonAsync<FileResponse>();

        file!.IsConfirmed.Should().BeTrue();
        file.ContentType.Should().Be("application/pdf");
        file.Width.Should().BeNull();
        file.Height.Should().BeNull();
        file.SizeBytes.Should().Be(SmallPdf.Length);
    }

    [Fact]
    public async Task Confirm_BytesDoNotMatchDeclaredType_IsBadRequest()
    {
        var ownerToken = await RegisterAsync("filepdf2", "filepdf2@test.com");
        var (guildId, _) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);

        Auth(ownerToken);
        var presignResp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/presign",
            new { filename = "fake.pdf", contentType = "application/pdf", sizeBytes = 16 }
        );
        presignResp.EnsureSuccessStatusCode();
        var presign = await presignResp.Content.ReadFromJsonAsync<PresignResponse>();

        // Declared application/pdf, but the bytes are not a PDF.
        using var http = new HttpClient();
        var content = new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes("not a pdf at all"));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        (await http.PutAsync(presign!.UploadUrl, content)).EnsureSuccessStatusCode();

        Auth(ownerToken);
        var confirmResp = await Client.PostAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/{presign.FileId}/confirm",
            null
        );
        confirmResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
    public async Task LargeImageUpload_GetsAWebpThumbnail_AndTheOriginalStaysUntouched()
    {
        var ownerToken = await RegisterAsync("filethumb1", "filethumb1@test.com");
        var (guildId, _) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);

        // A real 1600×1200 PNG — over the 1024px thumbnail threshold on both axes.
        byte[] bigPng;
        using (var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(1600, 1200))
        using (var ms = new MemoryStream())
        {
            await img.SaveAsPngAsync(ms);
            bigPng = ms.ToArray();
        }

        Auth(ownerToken);
        var presignResp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/presign",
            new { filename = "big.png", contentType = "image/png", sizeBytes = bigPng.Length }
        );
        presignResp.EnsureSuccessStatusCode();
        var presign = await presignResp.Content.ReadFromJsonAsync<PresignResponse>();

        using var http = new HttpClient();
        var content = new ByteArrayContent(bigPng);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        (await http.PutAsync(presign!.UploadUrl, content)).EnsureSuccessStatusCode();

        Auth(ownerToken);
        (await Client.PostAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/{presign.FileId}/confirm",
            null
        )).EnsureSuccessStatusCode();

        var resp = await Client.GetAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/{presign.FileId}"
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ThumbUrlResponse>();
        body!.ThumbnailUrl.Should().NotBeNullOrEmpty();

        // The thumbnail is a real WebP fitting 800×600.
        var thumbBytes = await http.GetByteArrayAsync(body.ThumbnailUrl);
        var thumbInfo = SixLabors.ImageSharp.Image.Identify(thumbBytes);
        thumbInfo.Width.Should().BeLessThanOrEqualTo(800);
        thumbInfo.Height.Should().BeLessThanOrEqualTo(600);
        SixLabors.ImageSharp.Image.DetectFormat(thumbBytes).Name.Should().Be("Webp");

        // The original is byte-for-byte untouched — downloads keep full quality.
        var originalBytes = await http.GetByteArrayAsync(body.Url);
        originalBytes.Should().BeEquivalentTo(bigPng);
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

    [Fact]
    public async Task BatchDownload_ReturnsConfirmedChannelFiles_OmittingForeignAndUnknownIds()
    {
        var ownerToken = await RegisterAsync("filebatch1", "filebatch1@test.com");
        var (guildId, _) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var otherChannelId = await CreateChannelAsync(ownerToken, guildId);
        var fileA = await UploadConfirmedFileAsync(ownerToken, guildId, channelId);
        var fileB = await UploadConfirmedFileAsync(ownerToken, guildId, channelId);
        var foreign = await UploadConfirmedFileAsync(ownerToken, guildId, otherChannelId);

        Auth(ownerToken);
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/batch",
            new { fileIds = new[] { fileA, fileB, foreign, 999_999_999L } }
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<List<BatchFileResponse>>();

        // The other channel's file and the unknown id are silently omitted, not an error.
        body.Should().HaveCount(2);
        body!.Select(f => f.Id).Should().BeEquivalentTo([fileA, fileB]);
        body.Should().OnlyContain(f => !string.IsNullOrEmpty(f.Url));

        // An empty id list is rejected by validation, not treated as a valid no-op.
        var empty = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/batch",
            new { fileIds = Array.Empty<long>() }
        );
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BatchDownload_Dm_ParticipantGetsUrls_NonParticipantForbidden()
    {
        var tokenA = await RegisterAsync("filebatchdm_a", "filebatchdm_a@test.com");
        var (_, userIdB) = await RegisterWithIdAsync("filebatchdm_b", "filebatchdm_b@test.com");
        var outsiderToken = await RegisterAsync("filebatchdm_c", "filebatchdm_c@test.com");

        Auth(tokenA);
        var dmResp = await Client.PostAsJsonAsync("/api/dm", new { targetUserId = userIdB });
        dmResp.EnsureSuccessStatusCode();
        var dm = await dmResp.Content.ReadFromJsonAsync<DmResponse>();
        var fileId = await UploadConfirmedDmFileAsync(tokenA, dm!.ChannelId);

        Auth(tokenA);
        var resp = await Client.PostAsJsonAsync(
            $"/api/dm/{dm.ChannelId}/files/batch",
            new { fileIds = new[] { fileId } }
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<List<BatchFileResponse>>();
        body.Should().ContainSingle().Which.Id.Should().Be(fileId);

        Auth(outsiderToken);
        var forbidden = await Client.PostAsJsonAsync(
            $"/api/dm/{dm.ChannelId}/files/batch",
            new { fileIds = new[] { fileId } }
        );
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SendMessage_WithConfirmedAttachment_Succeeds()
    {
        var ownerToken = await RegisterAsync("fileowner7", "fileowner7@test.com");
        var (guildId, _) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var fileId = await UploadConfirmedFileAsync(ownerToken, guildId, channelId);

        Auth(ownerToken);
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/messages",
            new { content = "look at this", attachmentIds = new[] { fileId } }
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<MessageSendResponse>();
        body!.AttachmentIds.Should().Contain(fileId);
    }

    [Fact]
    public async Task SendMessage_ImageOnly_EmptyContentWithAttachment_Succeeds()
    {
        var ownerToken = await RegisterAsync("fileowner8", "fileowner8@test.com");
        var (guildId, _) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var fileId = await UploadConfirmedFileAsync(ownerToken, guildId, channelId);

        Auth(ownerToken);
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/messages",
            new { content = "", attachmentIds = new[] { fileId } }
        );
        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task SendMessage_WithUnconfirmedAttachment_IsBadRequest()
    {
        var ownerToken = await RegisterAsync("fileowner9", "fileowner9@test.com");
        var (guildId, _) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);

        // Presign only — never confirmed.
        Auth(ownerToken);
        var presignResp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/files/presign",
            new { filename = "pixel.png", contentType = "image/png", sizeBytes = SmallPng.Length }
        );
        presignResp.EnsureSuccessStatusCode();
        var presign = await presignResp.Content.ReadFromJsonAsync<PresignResponse>();

        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/messages",
            new { content = "nope", attachmentIds = new[] { presign!.FileId } }
        );
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SendMessage_WithAnotherUsersAttachment_IsForbidden()
    {
        var ownerToken = await RegisterAsync("fileowner10", "fileowner10@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var fileId = await UploadConfirmedFileAsync(ownerToken, guildId, channelId);

        var memberToken = await RegisterAsync("filemember10", "filemember10@test.com");
        await JoinAsync(memberToken, invite);

        // The member can send here, but the attachment belongs to the owner.
        Auth(memberToken);
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/messages",
            new { content = "stealing your file", attachmentIds = new[] { fileId } }
        );
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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
        var (token, _) = await RegisterWithIdAsync(username, email);
        return token;
    }

    private async Task<(string token, long userId)> RegisterWithIdAsync(string username, string email)
    {
        var resp = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, email, password = "Password123!" }
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.User.Id);
    }

    /// <summary>DM twin of <see cref="UploadConfirmedFileAsync"/> (participant-gated routes).</summary>
    private async Task<long> UploadConfirmedDmFileAsync(string token, long channelId)
    {
        Auth(token);
        var presignResp = await Client.PostAsJsonAsync(
            $"/api/dm/{channelId}/files/presign",
            new { filename = "pixel.png", contentType = "image/png", sizeBytes = SmallPng.Length }
        );
        presignResp.EnsureSuccessStatusCode();
        var presign = await presignResp.Content.ReadFromJsonAsync<PresignResponse>();

        using var http = new HttpClient();
        var content = new ByteArrayContent(SmallPng);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        (await http.PutAsync(presign!.UploadUrl, content)).EnsureSuccessStatusCode();

        Auth(token);
        var confirmResp = await Client.PostAsync(
            $"/api/dm/{channelId}/files/{presign.FileId}/confirm",
            null
        );
        confirmResp.EnsureSuccessStatusCode();
        return presign.FileId;
    }

    private void Auth(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<(long guildId, string inviteCode)> CreateGuildAsync(string token)
    {
        Auth(token);
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "File Guild" });
        resp.EnsureSuccessStatusCode();
        var guild = await resp.Content.ReadFromJsonAsync<GuildResponse>();
        return (guild!.Id, await CreateInviteCodeAsync(guild.Id));
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
        var joinResp = await Client.PostAsJsonAsync($"/api/invites/{invite}/join", new { });
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

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record DmResponse(long ChannelId);

    private record BatchFileResponse(long Id, string Url);

    private record GuildResponse(long Id, string Name);

    private record ChannelResponse(long Id, long? GuildId, string Name, string Type);

    private record PresignResponse(long FileId, string UploadUrl, string ObjectKey, long ExpiresAt);

    private record FileUrlResponse(string Url, long ExpiresAt);

    private record ThumbUrlResponse(string Url, long ExpiresAt, string? ThumbnailUrl);

    private record MessageSendResponse(long MessageId, long[] AttachmentIds);

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
