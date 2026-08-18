using FluentAssertions;
using Harmony.API.Grains;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Orleans.Serialization;
using Orleans.TestingHost;

namespace Harmony.IntegrationTests.Grains;

/// <summary>
/// D4 grain tests — the in-memory permission resolver. Each test gets its own single-silo
/// <see cref="InProcessTestCluster"/> with the grain's repositories registered as Moq singletons
/// (the grain resolves them through its <c>IServiceScopeFactory</c>, so singletons suffice). Fresh
/// cluster + fresh mocks per test keeps snapshots isolated. This mirrors the resolution cases pinned
/// by <c>PermissionServiceTests</c> — both paths run the shared <c>PermissionResolver</c>, so they
/// must agree — plus the grain-specific hydration/invalidation behaviour.
/// </summary>
public class GuildGrainTests : IAsyncLifetime
{
    private const long GuildId = 100;
    private const long ChannelId = 200;
    private const long UserId = 1;
    private const long EveryoneRoleId = 500;

    private InProcessTestCluster _cluster = null!;
    private readonly Mock<IRoleRepository> _roles = new();
    private readonly Mock<IGuildRepository> _guilds = new();
    private readonly Mock<IChannelPermissionOverrideRepository> _overrides = new();

    public async Task InitializeAsync()
    {
        // Defaults: an @everyone role (DefaultEveryone bits), one plain member (UserId, not owner),
        // no explicitly-assigned roles, no channel overrides. Individual tests override before acting.
        _roles
            .Setup(r => r.GetByGuildAsync(GuildId))
            .ReturnsAsync(() => new List<Role> { EveryoneRole((long)Permission.DefaultEveryone) });
        _roles
            .Setup(r => r.GetRoleIdsByMemberAsync(GuildId))
            .ReturnsAsync(() => new Dictionary<long, List<long>>());
        _guilds
            .Setup(g => g.GetMembersAsync(GuildId))
            .ReturnsAsync(() => new List<GuildMember> { Member(UserId, owner: false) });
        _overrides
            .Setup(o => o.GetByChannelAsync(ChannelId))
            .ReturnsAsync(() => new List<ChannelPermissionOverride>());

        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        builder.ConfigureSilo((_, siloBuilder) => siloBuilder.AddMemoryGrainStorage("Default"));
        builder.ConfigureHost(hostBuilder =>
        {
            hostBuilder.Services.AddSingleton(_roles.Object);
            hostBuilder.Services.AddSingleton(_guilds.Object);
            hostBuilder.Services.AddSingleton(_overrides.Object);
            // The silo loads every Harmony.API grain, incl. ChannelGrain whose method takes a Domain
            // MessageSentEvent — so this cluster needs the same JSON serializer as Program.cs even
            // though the GuildGrain methods use only primitives (see UserGrainTests).
            hostBuilder.Services.AddSerializer(s =>
                s.AddJsonSerializer(type =>
                    type.Namespace?.StartsWith("Harmony.Domain.Interfaces") == true
                )
            );
        });

        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.DisposeAsync();

    private IGuildGrain Grain() => _cluster.Client.GetGrain<IGuildGrain>(GuildId);

    private static Role EveryoneRole(long bits) =>
        new()
        {
            Id = EveryoneRoleId,
            GuildId = GuildId,
            Name = "@everyone",
            IsDefault = true,
            PermissionBits = bits,
        };

    private static Role NamedRole(long id, long bits) =>
        new()
        {
            Id = id,
            GuildId = GuildId,
            Name = $"role-{id}",
            PermissionBits = bits,
        };

    private static GuildMember Member(long userId, bool owner) =>
        new()
        {
            GuildId = GuildId,
            UserId = userId,
            IsOwner = owner,
        };

    private static bool Has(long bits, Permission p) => (bits & (long)p) == (long)p;

    // -------------------------------------------------------------------------
    // Resolution (mirrors PermissionServiceTests — same shared resolver)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NonMember_ResolvesToZero()
    {
        // The default snapshot has only UserId as a member; user 2 is unknown.
        var bits = await Grain().ResolveBits(2, null);
        bits.Should().Be(0);
    }

    [Fact]
    public async Task Owner_ResolvesToAllPermissions()
    {
        _guilds
            .Setup(g => g.GetMembersAsync(GuildId))
            .ReturnsAsync(new List<GuildMember> { Member(UserId, owner: true) });

        var bits = await Grain().ResolveBits(UserId, ChannelId);

        Has(bits, Permission.Administrator).Should().BeTrue();
        Has(bits, Permission.ManageGuild).Should().BeTrue();
        Has(bits, Permission.BanMembers).Should().BeTrue();
    }

    [Fact]
    public async Task EveryoneBase_GrantsDefaultMemberSet_ButNotModeration()
    {
        var bits = await Grain().ResolveBits(UserId, null);

        Has(bits, Permission.SendMessage).Should().BeTrue();
        Has(bits, Permission.ViewChannel).Should().BeTrue();
        Has(bits, Permission.ManageMessages).Should().BeFalse();
        Has(bits, Permission.Administrator).Should().BeFalse();
    }

    [Fact]
    public async Task AssignedRole_OrsInAdditionalBits()
    {
        const long modRoleId = 600;
        _roles
            .Setup(r => r.GetByGuildAsync(GuildId))
            .ReturnsAsync(new List<Role>
            {
                EveryoneRole((long)Permission.DefaultEveryone),
                NamedRole(modRoleId, (long)Permission.ManageMessages),
            });
        _roles
            .Setup(r => r.GetRoleIdsByMemberAsync(GuildId))
            .ReturnsAsync(new Dictionary<long, List<long>> { [UserId] = [modRoleId] });

        var bits = await Grain().ResolveBits(UserId, null);

        Has(bits, Permission.SendMessage).Should().BeTrue();    // from @everyone
        Has(bits, Permission.ManageMessages).Should().BeTrue(); // from assigned role
    }

    [Fact]
    public async Task AdministratorRole_BypassesEvenChannelDenies()
    {
        const long adminRoleId = 600;
        _roles
            .Setup(r => r.GetByGuildAsync(GuildId))
            .ReturnsAsync(new List<Role>
            {
                EveryoneRole((long)Permission.DefaultEveryone),
                NamedRole(adminRoleId, (long)Permission.Administrator),
            });
        _roles
            .Setup(r => r.GetRoleIdsByMemberAsync(GuildId))
            .ReturnsAsync(new Dictionary<long, List<long>> { [UserId] = [adminRoleId] });
        _overrides
            .Setup(o => o.GetByChannelAsync(ChannelId))
            .ReturnsAsync(new List<ChannelPermissionOverride>
            {
                new() { TargetType = "role", TargetId = EveryoneRoleId, DenyBits = (long)Permission.ViewChannel },
            });

        var bits = await Grain().ResolveBits(UserId, ChannelId);

        Has(bits, Permission.ViewChannel).Should().BeTrue();
        Has(bits, Permission.Administrator).Should().BeTrue();
    }

    [Fact]
    public async Task EveryoneChannelOverride_DenyRemovesBaseBit()
    {
        _overrides
            .Setup(o => o.GetByChannelAsync(ChannelId))
            .ReturnsAsync(new List<ChannelPermissionOverride>
            {
                new() { TargetType = "role", TargetId = EveryoneRoleId, DenyBits = (long)Permission.SendMessage },
            });

        var bits = await Grain().ResolveBits(UserId, ChannelId);

        Has(bits, Permission.SendMessage).Should().BeFalse();
        Has(bits, Permission.ViewChannel).Should().BeTrue(); // untouched
    }

    [Fact]
    public async Task MemberOverride_AllowTakesPrecedenceOverEveryoneDeny()
    {
        _overrides
            .Setup(o => o.GetByChannelAsync(ChannelId))
            .ReturnsAsync(new List<ChannelPermissionOverride>
            {
                new() { TargetType = "role", TargetId = EveryoneRoleId, DenyBits = (long)Permission.SendMessage },
                new() { TargetType = "user", TargetId = UserId, AllowBits = (long)Permission.SendMessage },
            });

        var bits = await Grain().ResolveBits(UserId, ChannelId);

        Has(bits, Permission.SendMessage).Should().BeTrue();
    }

    [Fact]
    public async Task ChannelOverrides_AreIgnored_ForGuildLevelResolution()
    {
        await Grain().ResolveBits(UserId, channelId: null);

        _overrides.Verify(o => o.GetByChannelAsync(It.IsAny<long>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // FilterByPermission (the unread fan-out's batched gate)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FilterByPermission_KeepsOnlyGrantedUsers_AndPreservesOrder()
    {
        const long owner = 3;
        const long denied = 2; // not a member of this guild
        _guilds
            .Setup(g => g.GetMembersAsync(GuildId))
            .ReturnsAsync(new List<GuildMember>
            {
                Member(UserId, owner: false),
                Member(owner, owner: true),
            });

        var result = await Grain().FilterByPermission(
            [owner, denied, UserId],
            (long)Permission.ViewChannel,
            null
        );

        result.Should().Equal(owner, UserId);
    }

    [Fact]
    public async Task FilterByPermission_AgreesWithResolveBits_ForEveryCandidate()
    {
        // @everyone grants ViewChannel but not BanMembers, so the same member is kept for one and
        // dropped for the other — exactly as a per-user ResolveBits would decide.
        (await Grain().FilterByPermission([UserId], (long)Permission.ViewChannel, null))
            .Should()
            .Equal(UserId);
        (await Grain().FilterByPermission([UserId], (long)Permission.BanMembers, null))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task FilterByPermission_EmptyInput_ShortCircuits_WithoutTouchingRepositories()
    {
        (await Grain().FilterByPermission([], (long)Permission.ViewChannel, null)).Should().BeEmpty();

        _guilds.Verify(g => g.GetMembersAsync(It.IsAny<long>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // Hydration / invalidation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Snapshot_IsCached_HydratesOnceAcrossManyResolves()
    {
        await Grain().ResolveBits(UserId, null);
        await Grain().ResolveBits(UserId, null);
        await Grain().ResolveBits(UserId, null);

        // One hydration for the whole activation — later resolves are pure in-memory.
        _guilds.Verify(g => g.GetMembersAsync(GuildId), Times.Once);
        _roles.Verify(r => r.GetByGuildAsync(GuildId), Times.Once);
    }

    [Fact]
    public async Task InvalidateGuild_ForcesReload_PickingUpChangedRoleBits()
    {
        // Warm the snapshot: @everyone lacks ManageMessages.
        (await Grain().ResolveBits(UserId, null) & (long)Permission.ManageMessages).Should().Be(0);

        // @everyone gains ManageMessages, then the guild is invalidated.
        _roles
            .Setup(r => r.GetByGuildAsync(GuildId))
            .ReturnsAsync(new List<Role>
            {
                EveryoneRole((long)Permission.DefaultEveryone | (long)Permission.ManageMessages),
            });
        await Grain().InvalidateGuild();

        var bits = await Grain().ResolveBits(UserId, null);

        Has(bits, Permission.ManageMessages).Should().BeTrue();
        _roles.Verify(r => r.GetByGuildAsync(GuildId), Times.Exactly(2)); // reloaded
    }

    [Fact]
    public async Task InvalidateUser_ForcesReload_PickingUpChangedMembership()
    {
        // Warm the snapshot with user 2 absent → denied.
        (await Grain().ResolveBits(2, null)).Should().Be(0);

        // User 2 joins, then the (per-user) invalidation drops the snapshot.
        _guilds
            .Setup(g => g.GetMembersAsync(GuildId))
            .ReturnsAsync(new List<GuildMember>
            {
                Member(UserId, owner: false),
                Member(2, owner: false),
            });
        await Grain().InvalidateUser(2);

        var bits = await Grain().ResolveBits(2, null);

        Has(bits, Permission.ViewChannel).Should().BeTrue(); // now a member → @everyone applies
    }

    [Fact]
    public async Task InvalidatedOverride_IsReloaded_OnNextChannelResolve()
    {
        // Warm the channel override cache: @everyone denies SendMessage in the channel.
        _overrides
            .Setup(o => o.GetByChannelAsync(ChannelId))
            .ReturnsAsync(new List<ChannelPermissionOverride>
            {
                new() { TargetType = "role", TargetId = EveryoneRoleId, DenyBits = (long)Permission.SendMessage },
            });
        (await Grain().ResolveBits(UserId, ChannelId) & (long)Permission.SendMessage).Should().Be(0);

        // The override is removed, then the guild invalidation clears the cached overrides too.
        _overrides
            .Setup(o => o.GetByChannelAsync(ChannelId))
            .ReturnsAsync(new List<ChannelPermissionOverride>());
        await Grain().InvalidateGuild();

        var bits = await Grain().ResolveBits(UserId, ChannelId);

        Has(bits, Permission.SendMessage).Should().BeTrue(); // deny gone
    }
}
