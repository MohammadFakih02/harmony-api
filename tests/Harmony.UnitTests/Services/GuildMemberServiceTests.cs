using FluentAssertions;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Harmony.UnitTests.Services;

/// <summary>
/// Unit tests for GuildMemberService's hierarchy guard and side-effect orchestration
/// (state change → audit → broadcast). Permission-bit enforcement is the endpoint filter's
/// job and isn't exercised here.
/// </summary>
public class GuildMemberServiceTests
{
    private const long GuildId = 100;
    private const long ActorId = 1;
    private const long TargetId = 2;

    private static (
        GuildMemberService sut,
        Mock<IGuildRepository> guilds,
        Mock<IGuildBanRepository> bans,
        Mock<IPermissionService> perms,
        Mock<IAuditLogService> audit,
        Mock<IHubBroadcaster> broadcaster
    ) BuildSut()
    {
        var guilds = new Mock<IGuildRepository>();
        var bans = new Mock<IGuildBanRepository>();
        var users = new Mock<IUserRepository>();
        var perms = new Mock<IPermissionService>();
        var audit = new Mock<IAuditLogService>();
        var broadcaster = new Mock<IHubBroadcaster>();

        guilds.Setup(g => g.GetByIdAsync(GuildId)).ReturnsAsync(new Guild { Id = GuildId, MemberCount = 5 });

        var sut = new GuildMemberService(
            guilds.Object,
            bans.Object,
            users.Object,
            perms.Object,
            audit.Object,
            broadcaster.Object,
            NullLogger<GuildMemberService>.Instance
        );
        return (sut, guilds, bans, perms, audit, broadcaster);
    }

    private static void SetupTarget(Mock<IGuildRepository> guilds, bool isOwner = false) =>
        guilds
            .Setup(g => g.GetMemberAsync(GuildId, TargetId))
            .ReturnsAsync(new GuildMember { UserId = TargetId, GuildId = GuildId, IsOwner = isOwner });

    [Fact]
    public async Task Kick_Self_ThrowsArgument()
    {
        var (sut, _, _, _, _, _) = BuildSut();

        await FluentActions
            .Invoking(() => sut.KickAsync(GuildId, ActorId, ActorId))
            .Should()
            .ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Kick_OwnerTarget_ThrowsForbidden()
    {
        var (sut, guilds, _, _, _, _) = BuildSut();
        SetupTarget(guilds, isOwner: true);

        await FluentActions
            .Invoking(() => sut.KickAsync(GuildId, ActorId, TargetId))
            .Should()
            .ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Kick_NonMember_ThrowsNotFound()
    {
        var (sut, guilds, _, _, _, _) = BuildSut();
        guilds.Setup(g => g.GetMemberAsync(GuildId, TargetId)).ReturnsAsync((GuildMember?)null);

        await FluentActions
            .Invoking(() => sut.KickAsync(GuildId, ActorId, TargetId))
            .Should()
            .ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Kick_Happy_RemovesMember_Audits_Broadcasts_Invalidates()
    {
        var (sut, guilds, _, perms, audit, broadcaster) = BuildSut();
        SetupTarget(guilds);

        await sut.KickAsync(GuildId, ActorId, TargetId);

        guilds.Verify(g => g.RemoveMemberAsync(It.IsAny<GuildMember>()), Times.Once);
        guilds.Verify(g => g.SaveChangesAsync(), Times.Once);
        perms.Verify(p => p.InvalidateUserAsync(TargetId, GuildId, It.IsAny<CancellationToken>()), Times.Once);
        audit.Verify(
            a => a.LogAsync(
                GuildId, ActorId, AuditLogAction.MemberKick,
                It.IsAny<long?>(), It.IsAny<object?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
        broadcaster.Verify(
            b => b.BroadcastMemberRemovedAsync(GuildId, It.IsAny<MemberRemovedPayload>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
        broadcaster.Verify(
            b => b.BroadcastKickedAsync(TargetId, It.Is<KickedPayload>(p => !p.Banned), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task Ban_Happy_AddsBanRow_RemovesMember_BroadcastsBanned()
    {
        var (sut, guilds, bans, _, audit, broadcaster) = BuildSut();
        SetupTarget(guilds);

        await sut.BanAsync(GuildId, ActorId, TargetId, "spam");

        bans.Verify(
            b => b.AddAsync(It.Is<GuildBan>(x => x.UserId == TargetId && x.BannedBy == ActorId && x.Reason == "spam")),
            Times.Once
        );
        guilds.Verify(g => g.RemoveMemberAsync(It.IsAny<GuildMember>()), Times.Once);
        audit.Verify(
            a => a.LogAsync(
                GuildId, ActorId, AuditLogAction.MemberBan,
                It.IsAny<long?>(), It.IsAny<object?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
        broadcaster.Verify(
            b => b.BroadcastKickedAsync(TargetId, It.Is<KickedPayload>(p => p.Banned), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task Unban_NotBanned_ThrowsNotFound()
    {
        var (sut, _, bans, _, _, _) = BuildSut();
        bans.Setup(b => b.GetAsync(GuildId, TargetId)).ReturnsAsync((GuildBan?)null);

        await FluentActions
            .Invoking(() => sut.UnbanAsync(GuildId, ActorId, TargetId))
            .Should()
            .ThrowAsync<KeyNotFoundException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29L * 24 * 60 * 60)] // 29 days > 28-day cap
    public async Task Timeout_InvalidDuration_ThrowsArgument(long durationSeconds)
    {
        var (sut, guilds, _, _, _, _) = BuildSut();
        SetupTarget(guilds);

        await FluentActions
            .Invoking(() => sut.TimeoutAsync(GuildId, ActorId, TargetId, durationSeconds))
            .Should()
            .ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Timeout_Happy_SetsCommunicationDisabledUntil_AndBroadcasts()
    {
        var (sut, guilds, _, _, _, broadcaster) = BuildSut();
        var member = new GuildMember { UserId = TargetId, GuildId = GuildId, IsOwner = false };
        guilds.Setup(g => g.GetMemberAsync(GuildId, TargetId)).ReturnsAsync(member);

        await sut.TimeoutAsync(GuildId, ActorId, TargetId, 600);

        member.CommunicationDisabledUntil.Should().NotBeNull();
        guilds.Verify(g => g.SaveChangesAsync(), Times.Once);
        broadcaster.Verify(
            b => b.BroadcastMemberUpdatedAsync(
                GuildId,
                It.Is<MemberUpdatedPayload>(p => p.CommunicationDisabledUntil != null),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    // ---- Nicknames ----------------------------------------------------------

    [Fact]
    public async Task SetOwnNickname_Happy_SetsNickname_Broadcasts_NoAudit()
    {
        var (sut, guilds, _, _, audit, broadcaster) = BuildSut();
        var me = new GuildMember { UserId = ActorId, GuildId = GuildId };
        guilds.Setup(g => g.GetMemberAsync(GuildId, ActorId)).ReturnsAsync(me);

        await sut.SetOwnNicknameAsync(GuildId, ActorId, "  Ace  ");

        me.Nickname.Should().Be("Ace"); // trimmed
        guilds.Verify(g => g.SaveChangesAsync(), Times.Once);
        broadcaster.Verify(
            b => b.BroadcastMemberUpdatedAsync(
                GuildId,
                It.Is<MemberUpdatedPayload>(p => p.UserId == ActorId && p.Nickname == "Ace"),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
        // Self-service rename is not audited.
        audit.Verify(
            a => a.LogAsync(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<long?>(), It.IsAny<object?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task SetOwnNickname_Blank_ClearsToNull()
    {
        var (sut, guilds, _, _, _, broadcaster) = BuildSut();
        var me = new GuildMember { UserId = ActorId, GuildId = GuildId, Nickname = "Old" };
        guilds.Setup(g => g.GetMemberAsync(GuildId, ActorId)).ReturnsAsync(me);

        await sut.SetOwnNicknameAsync(GuildId, ActorId, "   ");

        me.Nickname.Should().BeNull();
        broadcaster.Verify(
            b => b.BroadcastMemberUpdatedAsync(
                GuildId,
                It.Is<MemberUpdatedPayload>(p => p.Nickname == null),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task SetOwnNickname_NonMember_ThrowsNotFound()
    {
        var (sut, guilds, _, _, _, _) = BuildSut();
        guilds.Setup(g => g.GetMemberAsync(GuildId, ActorId)).ReturnsAsync((GuildMember?)null);

        await FluentActions
            .Invoking(() => sut.SetOwnNicknameAsync(GuildId, ActorId, "Ace"))
            .Should()
            .ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task SetNickname_Mod_SetsNickname_Audits_Broadcasts()
    {
        var (sut, guilds, _, _, audit, broadcaster) = BuildSut();
        var target = new GuildMember { UserId = TargetId, GuildId = GuildId };
        guilds.Setup(g => g.GetMemberAsync(GuildId, TargetId)).ReturnsAsync(target);

        await sut.SetNicknameAsync(GuildId, ActorId, TargetId, "Troublemaker");

        target.Nickname.Should().Be("Troublemaker");
        audit.Verify(
            a => a.LogAsync(
                GuildId, ActorId, AuditLogAction.MemberNicknameUpdate,
                It.IsAny<long?>(), It.IsAny<object?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
        broadcaster.Verify(
            b => b.BroadcastMemberUpdatedAsync(
                GuildId,
                It.Is<MemberUpdatedPayload>(p => p.UserId == TargetId && p.Nickname == "Troublemaker"),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task SetNickname_Self_ThrowsArgument()
    {
        var (sut, _, _, _, _, _) = BuildSut();

        await FluentActions
            .Invoking(() => sut.SetNicknameAsync(GuildId, ActorId, ActorId, "Ace"))
            .Should()
            .ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SetNickname_OwnerTarget_ThrowsForbidden()
    {
        var (sut, guilds, _, _, _, _) = BuildSut();
        SetupTarget(guilds, isOwner: true);

        await FluentActions
            .Invoking(() => sut.SetNicknameAsync(GuildId, ActorId, TargetId, "Ace"))
            .Should()
            .ThrowAsync<UnauthorizedAccessException>();
    }
}
