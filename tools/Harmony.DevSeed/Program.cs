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
const string LoadTestChannelName = "loadtest";

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

// --load-test-users=N — provisions N throwaway accounts in the seed guild and dumps their JWTs
// for k6 (see load-tests/README.md). Separate from the normal seed: it needs the guild to already
// exist, and it deliberately does NOT touch the five permission-tier users the demo relies on.
var loadTestCount = ParseLoadTestCount(flags);

try
{
    if (loadTestCount is { } count)
        await SeedLoadTestUsersAsync(count);
    else
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

    // Invites are now managed rows (no permanent guild code): mint one, join members by redeeming it.
    var invite = await Post<InviteRef>($"/api/guilds/{guildId}/invites", new { }, owner.Token)
        ?? throw new Exception("invite create returned no body");
    foreach (var u in new[] { admin, member, muted, restricted })
        await PostRaw($"/api/invites/{invite.Code}/join", new { }, u.Token);
    Console.WriteLine($"✓ Guild '{GuildName}' created, 4 members joined");

    // Channels (owner has ManageChannels) — a lived-in layout: three categories, a spread of text
    // channels, and voice channels exercising every option (default / max-bitrate / user-capped /
    // min-bitrate). Positions are a running counter so the sidebar order matches creation order.
    var channelsPath = $"/api/guilds/{guildId}/channels";
    var pos = 0;
    async Task<ChannelRef> Text(string name, long categoryId) => (await Post<ChannelRef>(channelsPath,
        new { name, type = "text", position = pos++, categoryId }, owner.Token))!;
    async Task<ChannelRef> Category(string name) => (await Post<ChannelRef>(channelsPath,
        new { name, type = "category", position = pos++ }, owner.Token))!;
    async Task Voice(string name, long categoryId, int bitrate, int? userLimit = null) =>
        await Post<ChannelRef>(channelsPath,
            new { name, type = "voice", position = pos++, categoryId, bitrate, userLimit }, owner.Token);

    // Information
    var info = await Category("Information");
    var welcome = await Text("welcome", info.Id);
    var announcements = await Text("announcements", info.Id);
    var rules = await Text("rules", info.Id);

    // Text Channels
    var textCat = await Category("Text Channels");
    var general = await Text("general", textCat.Id);
    var random = await Text("random", textCat.Id);
    var offTopic = await Text("off-topic", textCat.Id);
    var staff = await Text("staff", textCat.Id);

    // Voice Channels — one per option so the channel-settings UI has something to show for each.
    var voiceCat = await Category("Voice Channels");
    await Voice("General", voiceCat.Id, bitrate: 64000);              // default
    await Voice("Music", voiceCat.Id, bitrate: 96000);               // max bitrate
    await Voice("Duo", voiceCat.Id, bitrate: 64000, userLimit: 2);   // capped at 2
    await Voice("AFK", voiceCat.Id, bitrate: 8000);                  // min bitrate
    Console.WriteLine("✓ Channels: Information / Text (general,random,off-topic,staff) / Voice (General,Music,Duo,AFK)");

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

    // Seed messages through the real send pipeline (RabbitMQ → consumer → Scylla). Say fire-and-
    // forgets; SayId captures the returned id (needed to attach seeded reactions).
    Task Say(long channelId, string content, string token) =>
        PostRaw($"/api/guilds/{guildId}/channels/{channelId}/messages", new { content }, token);
    async Task<long> SayId(long channelId, string content, string token) =>
        (await Post<MessageRef>($"/api/guilds/{guildId}/channels/{channelId}/messages",
            new { content }, token))!.MessageId;

    await Say(welcome.Id, "Welcome to the Harmony test server! 👋", owner.Token);
    await Say(welcome.Id, "Log in as each seeded user in a separate browser profile to see the permission tiers.", owner.Token);
    await Say(rules.Id, "1. Be excellent to each other.   2. This is a dev sandbox — expect resets.", owner.Token);
    await Say(announcements.Id, "📣 Voice, video and screenshare are live — hop into a Voice channel to try them.", owner.Token);

    var authors = new[] { owner, admin, member };
    string[] generalLines =
    {
        "Welcome to the Harmony test server! 👋",
        "This guild was provisioned by the dev seeder.",
        "owner sees everything; admin can manage; member is a plain user.",
        "muted is timed out — they can read but can't send anywhere.",
        "restricted can read #general but can't post here.",
        "#staff is invisible to everyone except owner + admin.",
        "Scroll up and down to exercise the message window.",
        "Send a message and watch it reconcile from optimistic → confirmed.",
        "Edit and delete your own messages to test those paths.",
        "React to a message — the pills below are seeded reactions. 🎉",
    };
    var generalIds = new List<long>();
    for (var i = 0; i < generalLines.Length; i++)
        generalIds.Add(await SayId(general.Id, generalLines[i], authors[i % authors.Length].Token));

    foreach (var line in new[] { "random channel chatter", "anyone here? 🎲", "ship it 🚢" })
        await Say(random.Id, line, member.Token);
    foreach (var line in new[] { "post your memes here 😹", "off-topic goes here", "what are you playing this week?" })
        await Say(offTopic.Id, line, admin.Token);
    Console.WriteLine("✓ Seeded messages across #welcome / #announcements / #rules / #general / #random / #off-topic");

    // Reactions — inserted straight into Postgres (deterministic, and no wait on the async Scylla
    // persist the reactable-message check would otherwise race).
    await SeedReactionsAsync(general.Id, generalIds, owner.Id, admin.Id, member.Id);
    Console.WriteLine("✓ Seeded reactions on #general messages");

    // Social graph — friendships + a 1:1 DM + a group DM, so /friends and DMs aren't empty on login.
    await SeedSocialAsync(owner, admin, member);

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
// Seed helpers (reactions + social graph)
// ---------------------------------------------------------------------------

// Reactions live in Postgres (no FK to the Scylla message), so we insert them directly — fast,
// deterministic, and immune to the send→consumer→Scylla lag. Fresh message ids every --reset mean
// no PK clash (old rows harmlessly orphan). The (message, emoji, user) triple is the composite PK.
async Task SeedReactionsAsync(long channelId, List<long> messageIds, long ownerId, long adminId, long memberId)
{
    if (messageIds.Count == 0)
        return;

    var options = new DbContextOptionsBuilder<HarmonyDbContext>().UseNpgsql(pgConn).Options;
    await using var db = new HarmonyDbContext(options);
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    (int Msg, string Emoji, long User)[] plan =
    {
        (0, "👋", ownerId), (0, "👋", memberId), (0, "🎉", adminId),
        (1, "👍", memberId),
        (2, "❤️", ownerId), (2, "😄", adminId),
        (9, "🎉", ownerId), (9, "🎉", memberId), (9, "🚀", adminId),
    };
    foreach (var (msg, emoji, user) in plan)
    {
        if (msg >= messageIds.Count)
            continue;
        db.MessageReactions.Add(new MessageReaction
        {
            MessageId = messageIds[msg],
            ChannelId = channelId,
            Emoji = emoji,
            UserId = user,
            CreatedAt = now,
        });
    }
    await db.SaveChangesAsync();
}

// Friendships + a 1:1 DM + a group DM. Idempotent across --reset: friends and DM channels persist on
// the users (only the guild is dropped on reset), so we skip re-seeding once the owner already has
// the seed friendship — which also prevents duplicate group DMs piling up on repeated resets.
async Task SeedSocialAsync(Seeded owner, Seeded admin, Seeded member)
{
    var friends = await Get<List<FriendRef>>("/api/friends", owner.Token) ?? [];
    if (friends.Any(f => f.Username == member.Username))
    {
        Console.WriteLine("✓ Social graph already present (friends/DMs persist across --reset) — skipped");
        return;
    }

    await Friend(owner, member);
    await Friend(owner, admin);
    await Friend(member, admin);

    var dm = await Post<DmRef>("/api/dm", new { targetUserId = member.Id }, owner.Token);
    if (dm is not null)
    {
        await DmSay(dm.ChannelId, "hey! this is a seeded 1:1 direct message 👋", owner.Token);
        await DmSay(dm.ChannelId, "nice — DMs work end to end", member.Token);
    }

    var group = await Post<DmRef>("/api/dm/group",
        new { userIds = new[] { member.Id, admin.Id } }, owner.Token);
    if (group is not null)
    {
        await DmSay(group.ChannelId, "group DM seeded — owner, member, admin 👋", owner.Token);
        await DmSay(group.ChannelId, "handy for testing group calls too 📞", member.Token);
        await DmSay(group.ChannelId, "agreed 🎉", admin.Token);
    }

    Console.WriteLine("✓ Seeded friendships + a 1:1 DM + a group DM");
}

// Establishes an accepted friendship: A requests B, then B requests A back — the API auto-accepts a
// mutual request (Discord behavior), so no request-id juggling. Conflicts are swallowed (idempotent).
async Task Friend(Seeded a, Seeded b)
{
    await TrySend("/api/friends/request", new { username = b.Username }, a.Token);
    await TrySend("/api/friends/request", new { username = a.Username }, b.Token);
}

Task DmSay(long channelId, string content, string token) =>
    PostRaw($"/api/dm/{channelId}/messages", new { content }, token);

// POST that swallows a non-2xx (used for idempotent social ops — e.g. an already-friends 409).
async Task TrySend(string path, object body, string token)
{
    try { await SendAsync<object>(HttpMethod.Post, path, body, token); }
    catch { /* idempotent seed — already-exists is fine */ }
}

// ---------------------------------------------------------------------------
// Load-test seeding (k6)
// ---------------------------------------------------------------------------

static int? ParseLoadTestCount(HashSet<string> flags)
{
    // "--load-test-users=N", not "--load-test-users N": a bare positional N would be swallowed by
    // the apiBase/pgConn positional slots above.
    var flag = flags.FirstOrDefault(f => f.StartsWith("--load-test-users=", StringComparison.Ordinal));
    if (flag is null)
        return null;

    var raw = flag["--load-test-users=".Length..];
    if (!int.TryParse(raw, out var count) || count < 1)
        throw new Exception($"--load-test-users needs a positive integer, got '{raw}'.");
    return count;
}

async Task SeedLoadTestUsersAsync(int count)
{
    var owner = await EnsureUser("seed_owner", "owner@harmony.dev");

    var guild = (await Get<List<GuildRef>>("/api/users/me/guilds", owner.Token) ?? [])
        .FirstOrDefault(g => g.Name == GuildName)
        ?? throw new Exception(
            $"the '{GuildName}' guild does not exist yet — run the plain seed first "
            + "(dotnet run --project tools/Harmony.DevSeed)."
        );

    // A dedicated channel keeps the load run's traffic out of the channels used for manual demos,
    // and gives the k6 fan-out scenario a single well-known target.
    var channels = await Get<List<ChannelRef>>($"/api/guilds/{guild.Id}/channels", owner.Token) ?? [];
    var channel =
        channels.FirstOrDefault(c => c.Name == LoadTestChannelName)
        ?? await Post<ChannelRef>(
            $"/api/guilds/{guild.Id}/channels",
            new { name = LoadTestChannelName, type = "text", position = 99 },
            owner.Token
        )
        ?? throw new Exception("load-test channel create returned no body");

    var invite = await Post<InviteRef>($"/api/guilds/{guild.Id}/invites", new { }, owner.Token)
        ?? throw new Exception("invite create returned no body");

    Console.WriteLine($"→ Seeding {count} load-test users into '{GuildName}' #{LoadTestChannelName}…");

    var users = new List<LoadUser>(count);
    for (var i = 1; i <= count; i++)
    {
        var u = await EnsureUser($"load_u{i}", $"load_u{i}@harmony.dev");
        // Idempotent: a re-run re-joins an already-joined user, which 409s harmlessly.
        await TrySend($"/api/invites/{invite.Code}/join", new { }, u.Token);
        // Snowflake ids are emitted as STRINGS: k6 runs on goja, whose numbers are float64, so a
        // 64-bit id parsed as a JS number silently loses its low bits and stops matching anything.
        users.Add(new LoadUser(u.Id.ToString(), u.Username, u.Token));

        if (i % 25 == 0 || i == count)
            Console.WriteLine($"  …{i}/{count}");
    }

    var outPath = Environment.GetEnvironmentVariable("SEED_LOAD_OUT") ?? "load-tests/users.json";
    var full = Path.GetFullPath(outPath);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    await File.WriteAllTextAsync(
        full,
        JsonSerializer.Serialize(
            new LoadFixture(apiBase, guild.Id.ToString(), channel.Id.ToString(), users),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }
        )
    );

    Console.WriteLine($"\n✓ {count} users ready → {full}");
    Console.WriteLine(
        "  Tokens expire per Jwt:AccessTokenExpiryMinutes (15 by default) — for a longer run, raise\n"
        + "  that key in appsettings.Development.json and re-seed, or the VUs will 401 mid-test.\n"
    );
}

// ---------------------------------------------------------------------------
// HTTP helpers
// ---------------------------------------------------------------------------
async Task<Seeded> EnsureUser(string username, string email)
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
        // Already registered (re-run) → log in instead. The field is "identifier", not "email":
        // LoginRequest takes an email-OR-username identifier (§5.33), so posting {email} bound
        // Identifier to null, tripped the NotEmpty validator, and 400'd — which made every re-run
        // (including --reset) throw on the very first seeded user.
        var login = await WithRetry(() => http.PostAsJsonAsync(
            "/api/auth/login", new { identifier = email, password = Password }, json));
        if (!login.IsSuccessStatusCode)
            throw new Exception($"register ({(int)reg.StatusCode}) and login ({(int)login.StatusCode}) "
                + $"both failed for {email}");
        ok = login;
    }

    var auth = await ok.Content.ReadFromJsonAsync<AuthResp>(json)
        ?? throw new Exception($"empty auth response for {email}");
    return new Seeded(username, auth.AccessToken, auth.User.Id);
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
    Console.WriteLine("  Channels: Information (welcome/announcements/rules) · Text (general/random/");
    Console.WriteLine("            off-topic/staff) · Voice (General/Music/Duo[cap 2]/AFK)");
    Console.WriteLine("  Social:   owner↔member↔admin friends · a 1:1 DM · a group DM · seeded reactions");
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
file record GuildRef(long Id, string Name);
file record InviteRef(string Code);
file record ChannelRef(long Id, string Name, string Type);
file record MessageRef(long MessageId);
file record DmRef(long ChannelId);
file record FriendRef(string Username);

/// <summary>A seeded user's identity — username (for friend requests), token, and id.</summary>
file record Seeded(string Username, string Token, long Id);

// --load-test-users output. Every id is a string on purpose — see SeedLoadTestUsersAsync.
file record LoadUser(string Id, string Username, string Token);

file record LoadFixture(string ApiBase, string GuildId, string ChannelId, List<LoadUser> Users);
