using FluentAssertions;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Harmony.UnitTests.Services;

/// <summary>
/// Unit tests for RoleService's two safety rules — hierarchy (act only on roles below your highest)
/// and the grant rule (only change permission bits you hold) — plus its side-effect orchestration.
/// </summary>
public class RoleServiceTests
{
    private const long GuildId = 100;
    private const long ActorId = 1;
    private const long RoleId = 50;

    private static readonly long AllBits =
        Enum.GetValues<Permission>().Aggregate(0L, (acc, p) => acc | (long)p);

    private static (
        RoleService sut,
        Mock<IGuildRepository> guilds,
        Mock<IRoleRepository> roles,
        Mock<IPermissionService> perms,
        Mock<IHubBroadcaster> broadcaster
    ) BuildSut(long ownerId = 999, long resolvedBits = -1, List<Role>? actorRoles = null)
    {
        if (resolvedBits == -1) resolvedBits = AllBits;

        var guilds = new Mock<IGuildRepository>();
        var roles = new Mock<IRoleRepository>();
        var perms = new Mock<IPermissionService>();
        var audit = new Mock<IAuditLogService>();
        var broadcaster = new Mock<IHubBroadcaster>();
        var snowflake = new Mock<ISnowflakeIdGenerator>();

        guilds.Setup(g => g.GetByIdAsync(GuildId)).ReturnsAsync(new Guild { Id = GuildId, OwnerId = ownerId });
        perms.Setup(p => p.ResolveAsync(ActorId, GuildId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedBits);
        roles.Setup(r => r.GetMemberRolesAsync(GuildId, ActorId)).ReturnsAsync(actorRoles ?? []);
        snowflake.Setup(s => s.NextId()).Returns(555);

        var sut = new RoleService(
            guilds.Object, roles.Object, perms.Object, audit.Object,
            broadcaster.Object, snowflake.Object, NullLogger<RoleService>.Instance
        );
        return (sut, guilds, roles, perms, broadcaster);
    }

    private static Role RoleAt(int position, bool isDefault = false, long bits = 0) =>
        new() { Id = RoleId, GuildId = GuildId, Name = "Mod", Position = position, IsDefault = isDefault, PermissionBits = bits };

    [Fact]
    public async Task Create_GrantingBitYouLack_Throws()
    {
        // Non-owner who only holds ManageRoles tries to mint a role with Administrator.
        var (sut, _, _, _, _) = BuildSut(resolvedBits: (long)Permission.ManageRoles);

        await FluentActions
            .Invoking(() => sut.CreateRoleAsync(GuildId, ActorId,
                new CreateRoleRequest("Admins", null, (long)Permission.Administrator, null, null)))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Create_Owner_PersistsAtBottom_AndBroadcasts()
    {
        var (sut, _, roles, _, broadcaster) = BuildSut(ownerId: ActorId);

        var resp = await sut.CreateRoleAsync(GuildId, ActorId,
            new CreateRoleRequest("Mods", 0xFF0000, (long)Permission.KickMembers, true, null));

        resp.Position.Should().Be(1); // created just above @everyone
        resp.PermissionBits.Should().Be((long)Permission.KickMembers);
        roles.Verify(r => r.AddAsync(It.IsAny<Role>()), Times.Once);
        broadcaster.Verify(b => b.BroadcastRoleCreatedAsync(GuildId, It.IsAny<Harmony.Application.DTOs.Responses.RoleResponse>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_WithNullBits_DefaultsToEveryonesPermissionSet()
    {
        // A new role with no explicit bits inherits @everyone's permission set (Discord default).
        var everyoneBits = (long)Permission.DefaultEveryone;
        var (sut, _, roles, _, _) = BuildSut(ownerId: ActorId);
        roles.Setup(r => r.GetDefaultRoleAsync(GuildId))
            .ReturnsAsync(new Role { Id = 7, GuildId = GuildId, IsDefault = true, PermissionBits = everyoneBits });

        var resp = await sut.CreateRoleAsync(GuildId, ActorId,
            new CreateRoleRequest("New Role", null, null, null, null));

        resp.PermissionBits.Should().Be(everyoneBits);
    }

    [Fact]
    public async Task Update_RoleAtOrAboveYourHighest_Throws()
    {
        // Actor's highest assigned role is position 2; target sits at position 5.
        var (sut, _, roles, _, _) = BuildSut(actorRoles: [new Role { Position = 2 }]);
        roles.Setup(r => r.GetByIdAsync(RoleId)).ReturnsAsync(RoleAt(5));

        await FluentActions
            .Invoking(() => sut.UpdateRoleAsync(GuildId, ActorId, RoleId, new UpdateRoleRequest("x", null, null, null, null)))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Update_BitsChanged_InvalidatesGuild_AndBroadcasts()
    {
        var (sut, _, roles, perms, broadcaster) = BuildSut(ownerId: ActorId);
        roles.Setup(r => r.GetByIdAsync(RoleId)).ReturnsAsync(RoleAt(3, bits: 0));

        await sut.UpdateRoleAsync(GuildId, ActorId, RoleId,
            new UpdateRoleRequest(null, null, (long)Permission.ManageMessages, null, null));

        perms.Verify(p => p.InvalidateGuildAsync(GuildId, It.IsAny<CancellationToken>()), Times.Once);
        broadcaster.Verify(b => b.BroadcastRoleUpdatedAsync(GuildId, It.IsAny<Harmony.Application.DTOs.Responses.RoleResponse>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_DefaultRole_Throws()
    {
        var (sut, _, roles, _, _) = BuildSut(ownerId: ActorId);
        roles.Setup(r => r.GetByIdAsync(RoleId)).ReturnsAsync(RoleAt(0, isDefault: true));

        await FluentActions
            .Invoking(() => sut.DeleteRoleAsync(GuildId, ActorId, RoleId))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Delete_Happy_RemovesRole_InvalidatesGuild_Broadcasts()
    {
        var (sut, _, roles, perms, broadcaster) = BuildSut(ownerId: ActorId);
        roles.Setup(r => r.GetByIdAsync(RoleId)).ReturnsAsync(RoleAt(3));

        await sut.DeleteRoleAsync(GuildId, ActorId, RoleId);

        roles.Verify(r => r.Remove(It.IsAny<Role>()), Times.Once);
        perms.Verify(p => p.InvalidateGuildAsync(GuildId, It.IsAny<CancellationToken>()), Times.Once);
        broadcaster.Verify(b => b.BroadcastRoleDeletedAsync(GuildId, It.IsAny<RoleDeletedPayload>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Assign_RoleAtOrAboveYourHighest_Throws()
    {
        var (sut, _, roles, _, _) = BuildSut(actorRoles: [new Role { Position = 2 }]);
        roles.Setup(r => r.GetByIdAsync(RoleId)).ReturnsAsync(RoleAt(5));

        await FluentActions
            .Invoking(() => sut.AssignRoleAsync(GuildId, ActorId, RoleId, 77))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Assign_Happy_AddsAssignment_InvalidatesUser_Broadcasts()
    {
        const long targetUser = 77;
        var (sut, guilds, roles, perms, broadcaster) = BuildSut(ownerId: ActorId);
        roles.Setup(r => r.GetByIdAsync(RoleId)).ReturnsAsync(RoleAt(3));
        guilds.Setup(g => g.GetMemberAsync(GuildId, targetUser))
            .ReturnsAsync(new GuildMember { UserId = targetUser, GuildId = GuildId });
        roles.Setup(r => r.GetAssignmentAsync(RoleId, targetUser)).ReturnsAsync((RoleAssignment?)null);
        roles.Setup(r => r.GetMemberRoleIdsAsync(GuildId, targetUser)).ReturnsAsync([RoleId]);

        await sut.AssignRoleAsync(GuildId, ActorId, RoleId, targetUser);

        roles.Verify(r => r.AddAssignmentAsync(It.Is<RoleAssignment>(a => a.RoleId == RoleId && a.UserId == targetUser)), Times.Once);
        perms.Verify(p => p.InvalidateUserAsync(targetUser, GuildId, It.IsAny<CancellationToken>()), Times.Once);
        broadcaster.Verify(b => b.BroadcastMemberRoleUpdatedAsync(GuildId, It.IsAny<MemberRoleUpdatedPayload>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
