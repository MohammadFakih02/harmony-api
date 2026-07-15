using FluentAssertions;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Redis;
using Harmony.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Harmony.UnitTests.Services;

/// <summary>
/// Resolution-algorithm unit tests. Redis is reported disconnected so the cache is
/// bypassed (fail-open) and every call exercises the pure computation.
/// </summary>
public class PermissionServiceTests
{
    private const long GuildId = 100;
    private const long ChannelId = 200;
    private const long UserId = 1;
    private const long EveryoneRoleId = 500;

    private static (
        PermissionService sut,
        Mock<IGuildRepository> guilds,
        Mock<IRoleRepository> roles,
        Mock<IChannelPermissionOverrideRepository> overrides
    ) BuildSut()
    {
        var provider = new Mock<IRedisConnectionProvider>();
        provider.Setup(p => p.IsConnected).Returns(false);
        provider.Setup(p => p.Connection).Returns((StackExchange.Redis.IConnectionMultiplexer?)null);

        var guilds = new Mock<IGuildRepository>();
        var roles = new Mock<IRoleRepository>();
        var overrides = new Mock<IChannelPermissionOverrideRepository>();

        // Sensible defaults: member exists (not owner), an @everyone role, no extra roles/overrides.
        guilds
            .Setup(g => g.GetMemberAsync(GuildId, UserId))
            .ReturnsAsync(new GuildMember { GuildId = GuildId, UserId = UserId, IsOwner = false });
        roles
            .Setup(r => r.GetDefaultRoleAsync(GuildId))
            .ReturnsAsync(EveryoneRole((long)Permission.DefaultEveryone));
        roles.Setup(r => r.GetMemberRolesAsync(GuildId, UserId)).ReturnsAsync(new List<Role>());
        overrides
            .Setup(o => o.GetByChannelAsync(ChannelId))
            .ReturnsAsync(new List<ChannelPermissionOverride>());

        var sut = new PermissionService(
            guilds.Object,
            roles.Object,
            overrides.Object,
            provider.Object,
            NullLogger<PermissionService>.Instance
        );

        return (sut, guilds, roles, overrides);
    }

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
        new() { Id = id, GuildId = GuildId, Name = $"role-{id}", PermissionBits = bits };

    private static bool Has(long bits, Permission p) => (bits & (long)p) == (long)p;

    [Fact]
    public async Task NonMember_ResolvesToZero()
    {
        var (sut, guilds, _, _) = BuildSut();
        guilds.Setup(g => g.GetMemberAsync(GuildId, UserId)).ReturnsAsync((GuildMember?)null);

        var bits = await sut.ResolveAsync(UserId, GuildId);

        bits.Should().Be(0);
    }

    [Fact]
    public async Task Owner_ResolvesToAllPermissions()
    {
        var (sut, guilds, _, _) = BuildSut();
        guilds
            .Setup(g => g.GetMemberAsync(GuildId, UserId))
            .ReturnsAsync(new GuildMember { GuildId = GuildId, UserId = UserId, IsOwner = true });

        var bits = await sut.ResolveAsync(UserId, GuildId, ChannelId);

        Has(bits, Permission.Administrator).Should().BeTrue();
        Has(bits, Permission.ManageGuild).Should().BeTrue();
        Has(bits, Permission.BanMembers).Should().BeTrue();
    }

    [Fact]
    public async Task EveryoneBase_GrantsDefaultMemberSet_ButNotModeration()
    {
        var (sut, _, _, _) = BuildSut();

        var bits = await sut.ResolveAsync(UserId, GuildId);

        Has(bits, Permission.SendMessage).Should().BeTrue();
        Has(bits, Permission.ViewChannel).Should().BeTrue();
        Has(bits, Permission.ManageMessages).Should().BeFalse();
        Has(bits, Permission.Administrator).Should().BeFalse();
    }

    [Fact]
    public async Task AssignedRole_OrsInAdditionalBits()
    {
        var (sut, _, roles, _) = BuildSut();
        roles
            .Setup(r => r.GetMemberRolesAsync(GuildId, UserId))
            .ReturnsAsync(new List<Role> { NamedRole(600, (long)Permission.ManageMessages) });

        var bits = await sut.ResolveAsync(UserId, GuildId);

        Has(bits, Permission.SendMessage).Should().BeTrue();   // from @everyone
        Has(bits, Permission.ManageMessages).Should().BeTrue(); // from assigned role
    }

    [Fact]
    public async Task AdministratorRole_BypassesEvenChannelDenies()
    {
        var (sut, _, roles, overrides) = BuildSut();
        roles
            .Setup(r => r.GetMemberRolesAsync(GuildId, UserId))
            .ReturnsAsync(new List<Role> { NamedRole(600, (long)Permission.Administrator) });
        // A deny that would matter for a normal member must be ignored for an admin.
        overrides
            .Setup(o => o.GetByChannelAsync(ChannelId))
            .ReturnsAsync(new List<ChannelPermissionOverride>
            {
                new() { TargetType = "role", TargetId = EveryoneRoleId, DenyBits = (long)Permission.ViewChannel },
            });

        var bits = await sut.ResolveAsync(UserId, GuildId, ChannelId);

        Has(bits, Permission.ViewChannel).Should().BeTrue();
        Has(bits, Permission.Administrator).Should().BeTrue();
    }

    [Fact]
    public async Task EveryoneChannelOverride_DenyRemovesBaseBit()
    {
        var (sut, _, _, overrides) = BuildSut();
        overrides
            .Setup(o => o.GetByChannelAsync(ChannelId))
            .ReturnsAsync(new List<ChannelPermissionOverride>
            {
                new() { TargetType = "role", TargetId = EveryoneRoleId, DenyBits = (long)Permission.SendMessage },
            });

        var bits = await sut.ResolveAsync(UserId, GuildId, ChannelId);

        Has(bits, Permission.SendMessage).Should().BeFalse();
        Has(bits, Permission.ViewChannel).Should().BeTrue(); // untouched
    }

    [Fact]
    public async Task MemberOverride_AllowTakesPrecedenceOverEveryoneDeny()
    {
        var (sut, _, _, overrides) = BuildSut();
        overrides
            .Setup(o => o.GetByChannelAsync(ChannelId))
            .ReturnsAsync(new List<ChannelPermissionOverride>
            {
                // @everyone denies SendMessage...
                new() { TargetType = "role", TargetId = EveryoneRoleId, DenyBits = (long)Permission.SendMessage },
                // ...but this member is explicitly allowed it (highest precedence).
                new() { TargetType = "user", TargetId = UserId, AllowBits = (long)Permission.SendMessage },
            });

        var bits = await sut.ResolveAsync(UserId, GuildId, ChannelId);

        Has(bits, Permission.SendMessage).Should().BeTrue();
    }

    [Fact]
    public async Task RoleOverride_AppliesOnlyToMembersWithThatRole()
    {
        var (sut, _, roles, overrides) = BuildSut();
        const long modRoleId = 600;
        roles
            .Setup(r => r.GetMemberRolesAsync(GuildId, UserId))
            .ReturnsAsync(new List<Role> { NamedRole(modRoleId, 0) });
        overrides
            .Setup(o => o.GetByChannelAsync(ChannelId))
            .ReturnsAsync(new List<ChannelPermissionOverride>
            {
                new() { TargetType = "role", TargetId = modRoleId, AllowBits = (long)Permission.ManageMessages },
            });

        var bits = await sut.ResolveAsync(UserId, GuildId, ChannelId);

        Has(bits, Permission.ManageMessages).Should().BeTrue();
    }

    [Fact]
    public async Task ChannelOverrides_AreIgnored_ForGuildLevelResolution()
    {
        var (sut, _, _, overrides) = BuildSut();
        var bits = await sut.ResolveAsync(UserId, GuildId, channelId: null);

        Has(bits, Permission.SendMessage).Should().BeTrue();
        overrides.Verify(o => o.GetByChannelAsync(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task HasAsync_ReflectsResolvedBits()
    {
        var (sut, _, _, _) = BuildSut();

        (await sut.HasAsync(UserId, GuildId, Permission.SendMessage)).Should().BeTrue();
        (await sut.HasAsync(UserId, GuildId, Permission.BanMembers)).Should().BeFalse();
    }

    // ---- FilterByPermissionAsync (the unread fan-out's batched gate) --------
    //
    // Redis reads as disconnected in this harness, so the batched cache read returns nothing and
    // every user falls through to the per-user resolve. That is the path worth pinning: it must
    // agree with HasAsync exactly, because it decides who accrues an unread badge.

    [Fact]
    public async Task FilterByPermission_KeepsOnlyGrantedUsers_AndPreservesOrder()
    {
        var (sut, guilds, roles, _) = BuildSut();
        const long denied = 2;
        const long owner = 3;

        // `denied` is not a member at all → resolves to 0 bits → filtered out.
        guilds.Setup(g => g.GetMemberAsync(GuildId, denied)).ReturnsAsync((GuildMember?)null);
        // `owner` bypasses everything.
        guilds
            .Setup(g => g.GetMemberAsync(GuildId, owner))
            .ReturnsAsync(new GuildMember { GuildId = GuildId, UserId = owner, IsOwner = true });

        var result = await sut.FilterByPermissionAsync(
            [owner, denied, UserId],
            GuildId,
            Permission.ViewChannel
        );

        result.Should().Equal(owner, UserId);
    }

    [Fact]
    public async Task FilterByPermission_AgreesWithHasAsync_ForEveryCandidate()
    {
        var (sut, _, roles, _) = BuildSut();
        // @everyone grants ViewChannel but not BanMembers (DefaultEveryone), so the same member
        // must be kept for one permission and dropped for the other.
        (await sut.FilterByPermissionAsync([UserId], GuildId, Permission.ViewChannel))
            .Should()
            .Equal(UserId);
        (await sut.FilterByPermissionAsync([UserId], GuildId, Permission.BanMembers))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task FilterByPermission_EmptyInput_ShortCircuits_WithoutTouchingRepositories()
    {
        var (sut, guilds, _, _) = BuildSut();

        (await sut.FilterByPermissionAsync([], GuildId, Permission.ViewChannel)).Should().BeEmpty();

        guilds.Verify(g => g.GetMemberAsync(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
    }
}
