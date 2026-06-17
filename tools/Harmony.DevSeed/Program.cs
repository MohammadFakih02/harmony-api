using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

// ---------------------------------------------------------------------------
// Harmony dev seeder — provisions one "Harmony Test Server" with members at
// deliberately different permission tiers, so logging in as each (separate
// browser profiles / incognito) is a live demo of the Phase 3 permission stack.
//
// Most of it drives the REAL HTTP API (register → guild → join → channels →
// overrides → messages), which doubles as a pipeline smoke test. Two things the
// public API can't express yet go straight to Postgres: a custom Admin role +
// assignment (no role CRUD endpoint) and the member timeout (no moderation
// endpoint). Re-runnable: if the seed guild already exists it just reprints the
// credentials and exits — it does NOT reset.
//
//   dotnet run --project harmony-api/tools/Harmony.DevSeed [apiBaseUrl] [pgConnString]
//   env overrides: SEED_API, SEED_PG
// ---------------------------------------------------------------------------

const string Password = "Password123!";
const string GuildName = "Harmony Test Server";

var flags = args.Where(a => a.StartsWith("--")).ToHashSet();
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
var reset = flags.Contains("--reset") || Environment.GetEnvironmentVariable("SEED_RESET") == "1";

var apiBase =
    Environment.GetEnvironmentVariable("SEED_API")
    ?? (positional.Length > 0 ? positional[0] : "http://localhost:5057");
var pgConn =
    Environment.GetEnvironmentVariable("SEED_PG")
    ?? (positional.Length > 1
        ? positional[1]
        : "Host=localhost;Port=5432;Database=harmony;Username=admin;Password=secret");

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
using var http = new HttpClient { BaseAddress = new Uri(apiBase), Timeout = TimeSpan.FromSeconds(30) };

Console.WriteLine($"→ API:      {apiBase}");
Console.WriteLine($"→ Postgres: {Mask(pgConn)}");
Console.WriteLine();

try
{
    await SeedAsync();
    return 0;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"\n✗ Could not reach the API at {apiBase}. Is it running (dotnet run) "
        + $"with the full docker stack up?\n  {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\n✗ Seed failed: {ex.Message}");
    return 1;
}

async Task SeedAsync()
{
    // The five tiers. Order matters only in that the owner is first.
    var owner = await EnsureUser("seed_owner", "owner@harmony.dev");

    // Idempotency: if the owner already has the seed guild, we've run before.
    var existing = (await Get<List<GuildRef>>("/api/users/me/guilds", owner.Token) ?? [])
        .FirstOrDefault(g => g.Name == GuildName);
    if (existing is not null)
    {
        if (!reset)
        {
            Console.WriteLine("✓ Seed guild already exists — nothing to do. Re-run with --reset to rebuild.\n");
            PrintCheatSheet(existing.Id);
            return;
        }
        // Owner delete cascades members/roles/channels/overrides (Scylla msgs orphan — fine for dev).
        await Delete($"/api/guilds/{existing.Id}", owner.Token);
        Console.WriteLine("✓ Removed existing seed guild (--reset)");
    }

    var admin = await EnsureUser("seed_admin", "admin@harmony.dev");
    var member = await EnsureUser("seed_member", "member@harmony.dev");
    var muted = await EnsureUser("seed_muted", "muted@harmony.dev");
    var restricted = await EnsureUser("seed_restricted", "restricted@harmony.dev");
    Console.WriteLine("✓ Users ready (owner, admin, member, muted, restricted)");

    // Guild + membership (real API).
    var guild = await Post<GuildRef>(
        "/api/guilds",
        new { name = GuildName, description = "Auto-seeded multi-tier test server." },
        owner.Token
    ) ?? throw new Exception("guild create returned no body");
    var guildId = guild.Id;

    foreach (var u in new[] { admin, member, muted, restricted })
        await PostRaw($"/api/guilds/join/{guild.InviteCode}", new { }, u.Token);
    Console.WriteLine($"✓ Guild '{GuildName}' created, 4 members joined");

    // Channels (owner has ManageChannels).
    var channelsPath = $"/api/guilds/{guildId}/channels";
    var category = (await Post<ChannelRef>(channelsPath,
        new { name = "Text Channels", type = "category", position = 0 }, owner.Token))!;
    var general = (await Post<ChannelRef>(channelsPath,
        new { name = "general", type = "text", position = 1, categoryId = category.Id }, owner.Token))!;
    var random = (await Post<ChannelRef>(channelsPath,
        new { name = "random", type = "text", position = 2, categoryId = category.Id }, owner.Token))!;
    var staff = (await Post<ChannelRef>(channelsPath,
        new { name = "staff", type = "text", position = 3, categoryId = category.Id }, owner.Token))!;
    Console.WriteLine("✓ Channels: #general, #random, #staff");

    // DB-only bits: Admin role + assignment, and the muted member's timeout.
    // Returns the @everyone role id (needed for the channel override below).
    var (everyoneRoleId, adminRoleId) = await ApplyDbTouchesAsync(guildId, admin.Id, muted.Id);
    Console.WriteLine("✓ Admin role assigned; muted member timed out (DB)");

    // Channel overrides (real API — the feature we just built).
    var view = (long)Permission.ViewChannel;
    var send = (long)Permission.SendMessage;
    // #staff: hidden from @everyone, visible to the Admin role.
    await Put($"/api/guilds/{guildId}/channels/{staff.Id}/overrides/{everyoneRoleId}",
        new { targetType = "role", allowBits = 0L, denyBits = view }, owner.Token);
    await Put($"/api/guilds/{guildId}/channels/{staff.Id}/overrides/{adminRoleId}",
        new { targetType = "role", allowBits = view, denyBits = 0L }, owner.Token);
    // #general: restricted user can read but not send.
    await Put($"/api/guilds/{guildId}/channels/{general.Id}/overrides/{restricted.Id}",
        new { targetType = "user", allowBits = 0L, denyBits = send }, owner.Token);
    Console.WriteLine("✓ Overrides: #staff hidden from @everyone (Admin allowed); restricted can't send in #general");

    // Seed messages through the real send pipeline (RabbitMQ → consumer → Scylla).
    var authors = new[] { (owner, "owner"), (admin, "admin"), (member, "member") };
    string[] generalLines =
    {
        "Welcome to the Harmony test server! 👋",
        "This guild was provisioned by the dev seeder.",
        "Try logging in as each of the seeded users in separate browser profiles.",
        "owner sees everything; admin can manage; member is a plain user.",
        "muted is timed out — they can read but can't send anywhere.",
        "restricted can read this channel but can't post here.",
        "#staff is invisible to everyone except owner + admin.",
        "Scroll up and down to exercise the virtual scroll.",
        "Send a message and watch it reconcile from optimistic → confirmed.",
        "Edit and delete your own messages to test those paths.",
    };
    for (var i = 0; i < generalLines.Length; i++)
    {
        var (author, _) = authors[i % authors.Length];
        await PostRaw($"/api/guilds/{guildId}/channels/{general.Id}/messages",
            new { content = generalLines[i] }, author.Token);
    }
    foreach (var line in new[] { "random channel chatter", "anyone here? 🎲", "ship it 🚢" })
        await PostRaw($"/api/guilds/{guildId}/channels/{random.Id}/messages",
            new { content = line }, member.Token);
    Console.WriteLine($"✓ Seeded {generalLines.Length} messages in #general, 3 in #random");

    Console.WriteLine();
    PrintCheatSheet(guildId);
}

// ---------------------------------------------------------------------------
// DB touches (no public API yet)
// ---------------------------------------------------------------------------
async Task<(long everyoneRoleId, long adminRoleId)> ApplyDbTouchesAsync(
    long guildId, long adminUserId, long mutedUserId)
{
    var options = new DbContextOptionsBuilder<HarmonyDbContext>().UseNpgsql(pgConn).Options;
    await using var db = new HarmonyDbContext(options);
    var snowflake = new SnowflakeIdGenerator();
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    var everyone = await db.GuildRoles.FirstAsync(r => r.GuildId == guildId && r.IsDefault);

    // Admin role: management bits on top of the default member set. Deliberately NOT
    // Administrator — we want the #staff override (not a bypass) to grant visibility.
    var adminBits = (long)(
        Permission.DefaultEveryone
        | Permission.ManageChannels | Permission.ManageMessages | Permission.ManageRoles
        | Permission.KickMembers | Permission.CreateInvite | Permission.ManageInvites
        | Permission.ViewAuditLog);

    var adminRole = new Role
    {
        Id = snowflake.NextId(),
        GuildId = guildId,
        Name = "Admin",
        Color = 0xE74C3C,
        PermissionBits = adminBits,
        Position = 1,
        IsHoisted = true,
        IsMentionable = true,
        IsDefault = false,
        CreatedAt = now,
    };
    db.GuildRoles.Add(adminRole);
    db.RoleAssignments.Add(new RoleAssignment
    {
        UserId = adminUserId,
        RoleId = adminRole.Id,
        GuildId = guildId,
        AssignedAt = now,
    });

    var mutedMember = await db.GuildMembers.FirstAsync(m =>
        m.GuildId == guildId && m.UserId == mutedUserId);
    mutedMember.CommunicationDisabledUntil =
        DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeMilliseconds();

    await db.SaveChangesAsync();
    return (everyone.Id, adminRole.Id);
}

// ---------------------------------------------------------------------------
// HTTP helpers
// ---------------------------------------------------------------------------
async Task<(string Token, long Id)> EnsureUser(string username, string email)
{
    var reg = await WithRetry(() => http.PostAsJsonAsync(
        "/api/auth/register", new { username, email, password = Password }, json));

    HttpResponseMessage ok;
    if (reg.IsSuccessStatusCode)
    {
        ok = reg;
    }
    else
    {
        // Already registered (re-run) → log in instead.
        var login = await WithRetry(() => http.PostAsJsonAsync(
            "/api/auth/login", new { email, password = Password }, json));
        if (!login.IsSuccessStatusCode)
            throw new Exception($"register ({(int)reg.StatusCode}) and login ({(int)login.StatusCode}) "
                + $"both failed for {email}");
        ok = login;
    }

    var auth = await ok.Content.ReadFromJsonAsync<AuthResp>(json)
        ?? throw new Exception($"empty auth response for {email}");
    return (auth.AccessToken, auth.User.Id);
}

async Task<T?> Get<T>(string path, string token) =>
    await SendAsync<T>(HttpMethod.Get, path, null, token);

async Task<T?> Post<T>(string path, object body, string token) =>
    await SendAsync<T>(HttpMethod.Post, path, body, token);

async Task PostRaw(string path, object body, string token) =>
    await SendAsync<object>(HttpMethod.Post, path, body, token);

async Task Put(string path, object body, string token) =>
    await SendAsync<object>(HttpMethod.Put, path, body, token);

async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, string token)
{
    using var res = await WithRetry(() =>
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            req.Content = JsonContent.Create(body, options: json);
        return http.SendAsync(req);
    });

    if (!res.IsSuccessStatusCode)
        throw new Exception($"{method} {path} → {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");

    if (typeof(T) == typeof(object))
        return default;
    return await res.Content.ReadFromJsonAsync<T>(json);
}

async Task Delete(string path, string token) =>
    await SendAsync<object>(HttpMethod.Delete, path, null, token);

// Retries on 429 (the API rate limiter) — honors Retry-After, else backs off ~2s.
// A fresh request is built per attempt (HttpRequestMessage can't be re-sent).
async Task<HttpResponseMessage> WithRetry(Func<Task<HttpResponseMessage>> send)
{
    for (var attempt = 1; ; attempt++)
    {
        var res = await send();
        if ((int)res.StatusCode != 429 || attempt >= 8)
            return res;

        var wait = res.Headers.RetryAfter?.Delta is { } d && d > TimeSpan.Zero
            ? d
            : TimeSpan.FromSeconds(2);
        res.Dispose();
        Console.WriteLine($"  …rate-limited, retrying in {wait.TotalSeconds:0}s (attempt {attempt}/8)");
        await Task.Delay(wait);
    }
}

static string Mask(string conn) =>
    System.Text.RegularExpressions.Regex.Replace(conn, "(?i)(password)=[^;]*", "$1=***");

void PrintCheatSheet(long guildId)
{
    Console.WriteLine("════════════════════════════════════════════════════════════════");
    Console.WriteLine($"  Guild: {GuildName}  (id {guildId})   ·   all passwords: {Password}");
    Console.WriteLine("════════════════════════════════════════════════════════════════");
    Console.WriteLine("  owner@harmony.dev       owner        → everything");
    Console.WriteLine("  admin@harmony.dev       Admin role   → manage channels/messages, sees #staff");
    Console.WriteLine("  member@harmony.dev      plain member → normal send/read; no #staff");
    Console.WriteLine("  muted@harmony.dev       timed out    → can read, CANNOT send anywhere (24h)");
    Console.WriteLine("  restricted@harmony.dev  override      → reads #general but CANNOT send there");
    Console.WriteLine("────────────────────────────────────────────────────────────────");
    Console.WriteLine("  Tip: log in as each in a SEPARATE browser profile / incognito");
    Console.WriteLine("       (the refresh-token cookie is per-profile — tabs share it).");
    Console.WriteLine("════════════════════════════════════════════════════════════════");
}

// ---------------------------------------------------------------------------
// Minimal response shapes (partial — extra JSON fields are ignored)
// ---------------------------------------------------------------------------
file record AuthResp(string AccessToken, UserRef User);
file record UserRef(long Id);
file record GuildRef(long Id, string Name, string? InviteCode);
file record ChannelRef(long Id, string Name, string Type);
