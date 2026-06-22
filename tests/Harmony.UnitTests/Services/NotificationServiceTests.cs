using FluentAssertions;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Harmony.UnitTests.Services;

/// <summary>
/// Unit tests for NotificationService's suppression chain. Friend-request notifications
/// here (2 guards: preference, then mute, then block); mention notifications — the
/// 4-guard chain (self, preference, mute x3, block) — live in the same fixture's
/// companion tests.
/// </summary>
public class NotificationServiceTests
{
    private const long AddresseeId = 1;
    private const long RequesterId = 2;
    private const long NotificationId = 999;

    private const long ActorId = 10;
    private const long MentionedUserId = 20;
    private const long MentionedUserId2 = 21;
    private const long GuildId = 100;
    private const long ChannelId = 200;
    private const long MessageId = 300;
    private const long CreatedAt = 12345;

    private static (
        NotificationService sut,
        Mock<INotificationRepository> notifications,
        Mock<IUserBlockRepository> blocks,
        Mock<IUserMuteRepository> mutes,
        Mock<IHubBroadcaster> broadcaster,
        Mock<INotificationPreferenceRepository> preferences
    ) BuildSut()
    {
        var notifications = new Mock<INotificationRepository>();
        var blocks = new Mock<IUserBlockRepository>();
        var mutes = new Mock<IUserMuteRepository>();
        var broadcaster = new Mock<IHubBroadcaster>();
        var preferences = new Mock<INotificationPreferenceRepository>();
        var snowflake = new Mock<ISnowflakeIdGenerator>();
        snowflake.Setup(s => s.NextId()).Returns(NotificationId);

        // Defaults so a test only has to override what it actually exercises:
        // no row (preferences.GetAsync → null defaults to "allowed", see GetAsync's
        // own doc comment), never muted, never blocked.
        preferences
            .Setup(p => p.GetAsync(It.IsAny<long>()))
            .ReturnsAsync((NotificationPreference?)null);
        // An unconfigured Moq call to a method returning a List<T> yields null, not an
        // empty list — CreateMentionNotificationsAsync calls .ToDictionary() on this
        // result, so a missing stub here throws ArgumentNullException, not "no rows."
        preferences
            .Setup(p => p.GetForUsersAsync(It.IsAny<List<long>>()))
            .ReturnsAsync(new List<NotificationPreference>());
        mutes
            .Setup(m =>
                m.IsMutedAsync(
                    It.IsAny<long>(),
                    It.IsAny<long>(),
                    It.IsAny<string>(),
                    It.IsAny<long>()
                )
            )
            .ReturnsAsync(false);
        blocks
            .Setup(b => b.AreBlockedAsync(It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(false);

        var sut = new NotificationService(
            notifications.Object,
            blocks.Object,
            mutes.Object,
            broadcaster.Object,
            preferences.Object,
            snowflake.Object,
            NullLogger<NotificationService>.Instance
        );

        return (sut, notifications, blocks, mutes, broadcaster, preferences);
    }

    [Fact]
    public async Task CreateFriendRequestNotificationAsync_HappyPath_PersistsAndBroadcasts()
    {
        var (sut, notifications, _, _, broadcaster, _) = BuildSut();

        await sut.CreateFriendRequestNotificationAsync(AddresseeId, RequesterId);

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Once);
        notifications.Verify(n => n.SaveChangesAsync(), Times.Once);
        broadcaster.Verify(
            b =>
                b.BroadcastNotificationReceivedAsync(
                    AddresseeId,
                    It.Is<NotificationPayload>(p =>
                        p.Id == NotificationId
                        && p.Type == "friend_request"
                        && p.ActorId == RequesterId
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task CreateFriendRequestNotificationAsync_WhenFriendRequestsDisabled_DoesNotCreate()
    {
        var (sut, notifications, _, _, broadcaster, preferences) = BuildSut();
        preferences
            .Setup(p => p.GetAsync(AddresseeId))
            .ReturnsAsync(
                new NotificationPreference { UserId = AddresseeId, FriendRequests = false }
            );

        await sut.CreateFriendRequestNotificationAsync(AddresseeId, RequesterId);

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
        broadcaster.Verify(
            b =>
                b.BroadcastNotificationReceivedAsync(
                    It.IsAny<long>(),
                    It.IsAny<NotificationPayload>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task CreateFriendRequestNotificationAsync_WhenAddresseeMutesRequester_DoesNotCreate()
    {
        var (sut, notifications, _, mutes, _, _) = BuildSut();
        // The mute check must be addressee-mutes-requester (it's the addressee's own
        // notification being suppressed) — assert that exact direction, not the reverse.
        mutes
            .Setup(m =>
                m.IsMutedAsync(AddresseeId, RequesterId, MuteTargetType.User, It.IsAny<long>())
            )
            .ReturnsAsync(true);

        await sut.CreateFriendRequestNotificationAsync(AddresseeId, RequesterId);

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task CreateFriendRequestNotificationAsync_WhenBlocked_DoesNotCreate()
    {
        var (sut, notifications, blocks, _, _, _) = BuildSut();
        blocks.Setup(b => b.AreBlockedAsync(AddresseeId, RequesterId)).ReturnsAsync(true);

        await sut.CreateFriendRequestNotificationAsync(AddresseeId, RequesterId);

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task CreateFriendRequestNotificationAsync_WhenBroadcastFails_StillPersistsAndDoesNotThrow()
    {
        var (sut, notifications, _, _, broadcaster, _) = BuildSut();
        broadcaster
            .Setup(b =>
                b.BroadcastNotificationReceivedAsync(
                    It.IsAny<long>(),
                    It.IsAny<NotificationPayload>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => sut.CreateFriendRequestNotificationAsync(AddresseeId, RequesterId);

        await act.Should().NotThrowAsync();
        notifications.Verify(n => n.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task CreateMentionNotificationAsync_HappyPath_PersistsAndBroadcasts()
    {
        var mentionedUserIds = new List<long> { MentionedUserId };
        var (sut, notifications, _, _, broadcaster, _) = BuildSut();
        await sut.CreateMentionNotificationsAsync(
            mentionedUserIds,
            ActorId,
            GuildId,
            ChannelId,
            MessageId,
            CreatedAt
        );
        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Once);
        notifications.Verify(n => n.SaveChangesAsync(), Times.Once);
        broadcaster.Verify(
            b =>
                b.BroadcastNotificationReceivedAsync(
                    MentionedUserId,
                    It.Is<NotificationPayload>(p =>
                        p.Id == NotificationId
                        && p.Type == "mention"
                        && p.ActorId == ActorId
                        && p.GuildId == GuildId
                        && p.ChannelId == ChannelId
                        && p.MessageId == MessageId
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task CreateMentionNotificationsAsync_SelfMention_DoesNotCreate()
    {
        var (sut, notifications, _, _, _, _) = BuildSut();
        // The actor mentioning themselves must be skipped — there is no "self" guard
        // anywhere else in the pipeline, so this has to be the service's job.
        var mentionedUserIds = new List<long> { ActorId };

        await sut.CreateMentionNotificationsAsync(
            mentionedUserIds,
            ActorId,
            GuildId,
            ChannelId,
            MessageId,
            CreatedAt
        );

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task CreateMentionNotificationsAsync_WhenMentionsDisabled_DoesNotCreate()
    {
        var (sut, notifications, _, _, _, preferences) = BuildSut();
        var mentionedUserIds = new List<long> { MentionedUserId };
        preferences
            .Setup(p => p.GetForUsersAsync(mentionedUserIds))
            .ReturnsAsync(
                new List<NotificationPreference>
                {
                    new() { UserId = MentionedUserId, MentionsEnabled = false },
                }
            );

        await sut.CreateMentionNotificationsAsync(
            mentionedUserIds,
            ActorId,
            GuildId,
            ChannelId,
            MessageId,
            CreatedAt
        );

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task CreateMentionNotificationsAsync_WhenMutedTheActorAsUser_DoesNotCreate()
    {
        var (sut, notifications, _, mutes, _, _) = BuildSut();
        var mentionedUserIds = new List<long> { MentionedUserId };
        mutes
            .Setup(m =>
                m.IsMutedAsync(MentionedUserId, ActorId, MuteTargetType.User, It.IsAny<long>())
            )
            .ReturnsAsync(true);

        await sut.CreateMentionNotificationsAsync(
            mentionedUserIds,
            ActorId,
            GuildId,
            ChannelId,
            MessageId,
            CreatedAt
        );

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task CreateMentionNotificationsAsync_WhenMutedTheChannel_DoesNotCreate()
    {
        var (sut, notifications, _, mutes, _, _) = BuildSut();
        var mentionedUserIds = new List<long> { MentionedUserId };
        mutes
            .Setup(m =>
                m.IsMutedAsync(
                    MentionedUserId,
                    ChannelId,
                    MuteTargetType.Channel,
                    It.IsAny<long>()
                )
            )
            .ReturnsAsync(true);

        await sut.CreateMentionNotificationsAsync(
            mentionedUserIds,
            ActorId,
            GuildId,
            ChannelId,
            MessageId,
            CreatedAt
        );

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task CreateMentionNotificationsAsync_WhenMutedTheGuild_DoesNotCreate()
    {
        var (sut, notifications, _, mutes, _, _) = BuildSut();
        var mentionedUserIds = new List<long> { MentionedUserId };
        mutes
            .Setup(m =>
                m.IsMutedAsync(MentionedUserId, GuildId, MuteTargetType.Guild, It.IsAny<long>())
            )
            .ReturnsAsync(true);

        await sut.CreateMentionNotificationsAsync(
            mentionedUserIds,
            ActorId,
            GuildId,
            ChannelId,
            MessageId,
            CreatedAt
        );

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task CreateMentionNotificationsAsync_WhenGuildIsNull_SkipsTheGuildMuteCheck()
    {
        // Guild-less (DM) channels have no guild to check — passing guildId: null must
        // not NRE on guildId.Value and must not call IsMutedAsync for MuteTargetType.Guild.
        var (sut, notifications, _, mutes, _, _) = BuildSut();
        var mentionedUserIds = new List<long> { MentionedUserId };

        await sut.CreateMentionNotificationsAsync(
            mentionedUserIds,
            ActorId,
            guildId: null,
            ChannelId,
            MessageId,
            CreatedAt
        );

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Once);
        mutes.Verify(
            m =>
                m.IsMutedAsync(
                    It.IsAny<long>(),
                    It.IsAny<long>(),
                    MuteTargetType.Guild,
                    It.IsAny<long>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task CreateMentionNotificationsAsync_WhenBlocked_DoesNotCreate()
    {
        var (sut, notifications, blocks, _, _, _) = BuildSut();
        var mentionedUserIds = new List<long> { MentionedUserId };
        blocks.Setup(b => b.AreBlockedAsync(ActorId, MentionedUserId)).ReturnsAsync(true);

        await sut.CreateMentionNotificationsAsync(
            mentionedUserIds,
            ActorId,
            GuildId,
            ChannelId,
            MessageId,
            CreatedAt
        );

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task CreateMentionNotificationsAsync_TwoMentioned_OneSuppressedOneNot_OnlyNotifiesTheSurvivor()
    {
        // Proves the loop doesn't bail out of the whole batch on the first suppressed
        // recipient — each mentioned user is independently evaluated.
        var (sut, notifications, blocks, _, broadcaster, _) = BuildSut();
        var mentionedUserIds = new List<long> { MentionedUserId, MentionedUserId2 };
        blocks.Setup(b => b.AreBlockedAsync(ActorId, MentionedUserId)).ReturnsAsync(true);

        await sut.CreateMentionNotificationsAsync(
            mentionedUserIds,
            ActorId,
            GuildId,
            ChannelId,
            MessageId,
            CreatedAt
        );

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Once);
        broadcaster.Verify(
            b =>
                b.BroadcastNotificationReceivedAsync(
                    MentionedUserId2,
                    It.IsAny<NotificationPayload>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        broadcaster.Verify(
            b =>
                b.BroadcastNotificationReceivedAsync(
                    MentionedUserId,
                    It.IsAny<NotificationPayload>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }
}
