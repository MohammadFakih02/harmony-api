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
        Mock<INotificationPreferenceRepository> preferences,
        Mock<IPresenceService> presence,
        Mock<INotificationSettingRepository> settings,
        Mock<IPushOutboxRepository> pushOutbox
    ) BuildSut()
    {
        var notifications = new Mock<INotificationRepository>();
        var blocks = new Mock<IUserBlockRepository>();
        var mutes = new Mock<IUserMuteRepository>();
        var broadcaster = new Mock<IHubBroadcaster>();
        var preferences = new Mock<INotificationPreferenceRepository>();
        var settings = new Mock<INotificationSettingRepository>();
        var presence = new Mock<IPresenceService>();
        var pushOutbox = new Mock<IPushOutboxRepository>();
        var pushNudge = new Mock<IPushDispatchNudge>();
        var snowflake = new Mock<ISnowflakeIdGenerator>();
        snowflake.Setup(s => s.NextId()).Returns(NotificationId);

        // No per-guild/channel notification overrides by default → every recipient resolves to the
        // default level ("mentions"), so mentions pass. (A missing List<T> stub returns null, which
        // the resolver's foreach would NRE on — mirror the GetForUsersAsync empty-list default above.)
        settings
            .Setup(s =>
                s.GetForResolutionAsync(It.IsAny<List<long>>(), It.IsAny<long>(), It.IsAny<long>())
            )
            .ReturnsAsync(new List<NotificationSetting>());

        // Default the recipient to a non-DnD status so the live push fires (DnD suppression
        // is exercised explicitly in its own test).
        presence
            .Setup(p => p.GetStatusAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("online");

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
            settings.Object,
            presence.Object,
            pushOutbox.Object,
            pushNudge.Object,
            snowflake.Object,
            NullLogger<NotificationService>.Instance
        );

        return (
            sut,
            notifications,
            blocks,
            mutes,
            broadcaster,
            preferences,
            presence,
            settings,
            pushOutbox
        );
    }

    [Fact]
    public async Task CreateFriendRequestNotificationAsync_HappyPath_PersistsAndBroadcasts()
    {
        var (sut, notifications, _, _, broadcaster, _, _, _, _) = BuildSut();

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
    public async Task CreateFriendRequestNotificationAsync_WhenRecipientInDnd_PersistsButDoesNotBroadcast()
    {
        var (sut, notifications, _, _, broadcaster, _, presence, _, _) = BuildSut();
        // DnD suppresses the live interruption only — the row is still saved so the user
        // can catch up after leaving DnD.
        presence
            .Setup(p => p.GetStatusAsync(AddresseeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("dnd");

        await sut.CreateFriendRequestNotificationAsync(AddresseeId, RequesterId);

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Once);
        notifications.Verify(n => n.SaveChangesAsync(), Times.Once);
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
    public async Task CreateFriendRequestNotificationAsync_WhenFriendRequestsDisabled_DoesNotCreate()
    {
        var (sut, notifications, _, _, broadcaster, preferences, _, _, _) = BuildSut();
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
        var (sut, notifications, _, mutes, _, _, _, _, _) = BuildSut();
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
        var (sut, notifications, blocks, _, _, _, _, _, _) = BuildSut();
        blocks.Setup(b => b.AreBlockedAsync(AddresseeId, RequesterId)).ReturnsAsync(true);

        await sut.CreateFriendRequestNotificationAsync(AddresseeId, RequesterId);

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task CreateFriendRequestNotificationAsync_WhenBroadcastFails_StillPersistsAndDoesNotThrow()
    {
        var (sut, notifications, _, _, broadcaster, _, _, _, _) = BuildSut();
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
        var (sut, notifications, _, _, broadcaster, _, _, _, _) = BuildSut();
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
        var (sut, notifications, _, _, _, _, _, _, _) = BuildSut();
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
        var (sut, notifications, _, _, _, preferences, _, _, _) = BuildSut();
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
        var (sut, notifications, _, mutes, _, _, _, _, _) = BuildSut();
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
        var (sut, notifications, _, mutes, _, _, _, _, _) = BuildSut();
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
    public async Task CreateMentionNotificationsAsync_WhenMutedTheGuild_StillCreates()
    {
        // #9: muting a server silences its regular/all-level messages but a direct @mention must
        // still reach the recipient — so a guild mute does NOT suppress a mention, and the producer
        // doesn't even consult the guild-mute flag.
        var (sut, notifications, _, mutes, _, _, _, _, _) = BuildSut();
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

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Once);
        mutes.Verify(
            m => m.IsMutedAsync(It.IsAny<long>(), GuildId, MuteTargetType.Guild, It.IsAny<long>()),
            Times.Never
        );
    }

    [Fact]
    public async Task CreateMentionNotificationsAsync_WhenGuildIsNull_SkipsTheGuildMuteCheck()
    {
        // Guild-less (DM) channels have no guild to check — passing guildId: null must
        // not NRE on guildId.Value and must not call IsMutedAsync for MuteTargetType.Guild.
        var (sut, notifications, _, mutes, _, _, _, _, _) = BuildSut();
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
        var (sut, notifications, blocks, _, _, _, _, _, _) = BuildSut();
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
        var (sut, notifications, blocks, _, broadcaster, _, _, _, _) = BuildSut();
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

    [Fact]
    public async Task CreateMentionNotificationsAsync_GuildLevelNothing_SuppressesTheMention()
    {
        var (sut, notifications, _, _, _, _, _, settings, _) = BuildSut();
        // A guild-scope "nothing" with no channel override → the mention is silenced.
        settings
            .Setup(s => s.GetForResolutionAsync(It.IsAny<List<long>>(), GuildId, ChannelId))
            .ReturnsAsync(
                new List<NotificationSetting>
                {
                    new()
                    {
                        UserId = MentionedUserId,
                        ScopeType = NotificationScope.Guild,
                        ScopeId = GuildId,
                        Level = NotificationLevel.Nothing,
                    },
                }
            );

        await sut.CreateMentionNotificationsAsync(
            new List<long> { MentionedUserId },
            ActorId,
            GuildId,
            ChannelId,
            MessageId,
            CreatedAt
        );

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task CreateMentionNotificationsAsync_ChannelLevelOverridesGuildLevel_NotifiesWhenChannelAllows()
    {
        var (sut, notifications, _, _, _, _, _, settings, _) = BuildSut();
        // Guild scope says "nothing" but a channel-scope override says "mentions" — channel wins,
        // so the mention goes through.
        settings
            .Setup(s => s.GetForResolutionAsync(It.IsAny<List<long>>(), GuildId, ChannelId))
            .ReturnsAsync(
                new List<NotificationSetting>
                {
                    new()
                    {
                        UserId = MentionedUserId,
                        ScopeType = NotificationScope.Guild,
                        ScopeId = GuildId,
                        Level = NotificationLevel.Nothing,
                    },
                    new()
                    {
                        UserId = MentionedUserId,
                        ScopeType = NotificationScope.Channel,
                        ScopeId = ChannelId,
                        Level = NotificationLevel.Mentions,
                    },
                }
            );

        await sut.CreateMentionNotificationsAsync(
            new List<long> { MentionedUserId },
            ActorId,
            GuildId,
            ChannelId,
            MessageId,
            CreatedAt
        );

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Once);
    }

    // ------------------------------------------------------------------
    // Reply notifications — the "reply" chain (self, RepliesEnabled, level,
    // mutes, block), one recipient per call.
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateReplyNotificationAsync_HappyPath_PersistsAndPushes()
    {
        var (sut, notifications, _, _, broadcaster, _, _, _, _) = BuildSut();

        await sut.CreateReplyNotificationAsync(
            MentionedUserId, ActorId, GuildId, ChannelId, MessageId, CreatedAt);

        notifications.Verify(
            n => n.AddAsync(It.Is<Notification>(x =>
                x.UserId == MentionedUserId && x.Type == "reply" && x.ActorId == ActorId
                && x.ChannelId == ChannelId && x.MessageId == MessageId)),
            Times.Once);
        notifications.Verify(n => n.SaveChangesAsync(), Times.Once);
        broadcaster.Verify(
            b => b.BroadcastNotificationReceivedAsync(
                MentionedUserId, It.IsAny<NotificationPayload>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateReplyNotificationAsync_SelfReply_DoesNotCreate()
    {
        var (sut, notifications, _, _, _, _, _, _, _) = BuildSut();

        await sut.CreateReplyNotificationAsync(
            ActorId, ActorId, GuildId, ChannelId, MessageId, CreatedAt);

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task CreateReplyNotificationAsync_RepliesDisabled_DoesNotCreate()
    {
        var (sut, notifications, _, _, _, preferences, _, _, _) = BuildSut();
        preferences
            .Setup(p => p.GetAsync(MentionedUserId))
            .ReturnsAsync(new NotificationPreference { UserId = MentionedUserId, RepliesEnabled = false });

        await sut.CreateReplyNotificationAsync(
            MentionedUserId, ActorId, GuildId, ChannelId, MessageId, CreatedAt);

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task CreateReplyNotificationAsync_ChannelLevelNothing_DoesNotCreate()
    {
        var (sut, notifications, _, _, _, _, _, settings, _) = BuildSut();
        settings
            .Setup(s => s.GetForResolutionAsync(It.IsAny<List<long>>(), GuildId, ChannelId))
            .ReturnsAsync(
                new List<NotificationSetting>
                {
                    new()
                    {
                        UserId = MentionedUserId,
                        ScopeType = NotificationScope.Channel,
                        ScopeId = ChannelId,
                        Level = NotificationLevel.Nothing,
                    },
                }
            );

        await sut.CreateReplyNotificationAsync(
            MentionedUserId, ActorId, GuildId, ChannelId, MessageId, CreatedAt);

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task CreateReplyNotificationAsync_Blocked_DoesNotCreate()
    {
        var (sut, notifications, blocks, _, _, _, _, _, _) = BuildSut();
        blocks.Setup(b => b.AreBlockedAsync(ActorId, MentionedUserId)).ReturnsAsync(true);

        await sut.CreateReplyNotificationAsync(
            MentionedUserId, ActorId, GuildId, ChannelId, MessageId, CreatedAt);

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    // ---- transactional push outbox: one staged row per SURVIVING notification, ----
    // ---- added before the same SaveChangesAsync (never for a suppressed one).  ----

    [Fact]
    public async Task CreateFriendRequestNotificationAsync_StagesAPushOutboxRow_InTheSameSave()
    {
        var (sut, notifications, _, _, _, _, _, _, pushOutbox) = BuildSut();

        // The outbox Add must land before the commit — enforce ordering by failing the
        // test if SaveChangesAsync runs first.
        var saved = false;
        notifications
            .Setup(n => n.SaveChangesAsync())
            .Callback(() => saved = true)
            .Returns(Task.CompletedTask);
        var stagedBeforeSave = false;
        pushOutbox
            .Setup(p => p.AddAsync(It.IsAny<PushOutboxMessage>()))
            .Callback(() => stagedBeforeSave = !saved)
            .Returns(Task.CompletedTask);

        await sut.CreateFriendRequestNotificationAsync(AddresseeId, RequesterId);

        stagedBeforeSave.Should().BeTrue();
        pushOutbox.Verify(
            p =>
                p.AddAsync(
                    It.Is<PushOutboxMessage>(m =>
                        m.Kind == "friend_request"
                        && m.RecipientId == AddresseeId
                        && m.ActorId == RequesterId
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task CreateFriendRequestNotificationAsync_WhenSuppressed_StagesNoOutboxRow()
    {
        var (sut, _, blocks, _, _, _, _, _, pushOutbox) = BuildSut();
        blocks.Setup(b => b.AreBlockedAsync(AddresseeId, RequesterId)).ReturnsAsync(true);

        await sut.CreateFriendRequestNotificationAsync(AddresseeId, RequesterId);

        pushOutbox.Verify(p => p.AddAsync(It.IsAny<PushOutboxMessage>()), Times.Never);
    }

    [Fact]
    public async Task CreateMentionNotificationsAsync_StagesOutboxRows_OnlyForSurvivors()
    {
        var (sut, _, blocks, _, _, _, _, _, pushOutbox) = BuildSut();
        blocks.Setup(b => b.AreBlockedAsync(ActorId, MentionedUserId)).ReturnsAsync(true);

        await sut.CreateMentionNotificationsAsync(
            new List<long> { MentionedUserId, MentionedUserId2 },
            ActorId,
            GuildId,
            ChannelId,
            MessageId,
            CreatedAt
        );

        pushOutbox.Verify(
            p =>
                p.AddAsync(
                    It.Is<PushOutboxMessage>(m =>
                        m.Kind == "mention"
                        && m.RecipientId == MentionedUserId2
                        && m.ChannelId == ChannelId
                        && m.MessageId == MessageId
                    )
                ),
            Times.Once
        );
        pushOutbox.Verify(p => p.AddAsync(It.IsAny<PushOutboxMessage>()), Times.Once);
    }

    [Fact]
    public async Task CreateReplyNotificationAsync_StagesAReplyOutboxRow()
    {
        var (sut, _, _, _, _, _, _, _, pushOutbox) = BuildSut();

        await sut.CreateReplyNotificationAsync(
            MentionedUserId, ActorId, GuildId, ChannelId, MessageId, CreatedAt);

        pushOutbox.Verify(
            p =>
                p.AddAsync(
                    It.Is<PushOutboxMessage>(m =>
                        m.Kind == "reply" && m.RecipientId == MentionedUserId
                    )
                ),
            Times.Once
        );
    }

    // ------------------------------------------------------------------
    // Suppress-@everyone: a recipient reached ONLY via @everyone/@here who
    // opted out of broadcast pings in this scope is skipped; a direct mention
    // (never in everyoneOriginIds) still notifies. Channel-scope wins.
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateMentionNotificationsAsync_EveryoneOriginRecipient_WhoSuppressedEveryone_IsSkipped()
    {
        var (sut, notifications, _, _, _, _, _, settings, _) = BuildSut();
        settings
            .Setup(s => s.GetForResolutionAsync(It.IsAny<List<long>>(), GuildId, ChannelId))
            .ReturnsAsync(
                new List<NotificationSetting>
                {
                    new()
                    {
                        UserId = MentionedUserId,
                        ScopeType = NotificationScope.Guild,
                        ScopeId = GuildId,
                        Level = NotificationLevel.Mentions,
                        SuppressEveryone = true,
                    },
                }
            );

        await sut.CreateMentionNotificationsAsync(
            new List<long> { MentionedUserId },
            ActorId,
            GuildId,
            ChannelId,
            MessageId,
            CreatedAt,
            everyoneOriginIds: new List<long> { MentionedUserId }
        );

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task CreateMentionNotificationsAsync_DirectMention_StillNotifies_EvenWhenSuppressEveryone()
    {
        var (sut, notifications, _, _, _, _, _, settings, _) = BuildSut();
        settings
            .Setup(s => s.GetForResolutionAsync(It.IsAny<List<long>>(), GuildId, ChannelId))
            .ReturnsAsync(
                new List<NotificationSetting>
                {
                    new()
                    {
                        UserId = MentionedUserId,
                        ScopeType = NotificationScope.Guild,
                        ScopeId = GuildId,
                        Level = NotificationLevel.Mentions,
                        SuppressEveryone = true,
                    },
                }
            );

        // The user is mentioned but NOT via @everyone (empty everyoneOriginIds) — a direct @user
        // ping is never suppressed by the @everyone opt-out.
        await sut.CreateMentionNotificationsAsync(
            new List<long> { MentionedUserId },
            ActorId,
            GuildId,
            ChannelId,
            MessageId,
            CreatedAt,
            everyoneOriginIds: new List<long>()
        );

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Once);
    }

    [Fact]
    public async Task CreateMentionNotificationsAsync_ChannelSuppressOverridesGuild_NotifiesWhenChannelDoesNotSuppress()
    {
        var (sut, notifications, _, _, _, _, _, settings, _) = BuildSut();
        // Guild scope suppresses @everyone but a channel-scope row does not — channel wins,
        // so the @everyone-origin ping goes through.
        settings
            .Setup(s => s.GetForResolutionAsync(It.IsAny<List<long>>(), GuildId, ChannelId))
            .ReturnsAsync(
                new List<NotificationSetting>
                {
                    new()
                    {
                        UserId = MentionedUserId,
                        ScopeType = NotificationScope.Guild,
                        ScopeId = GuildId,
                        Level = NotificationLevel.Mentions,
                        SuppressEveryone = true,
                    },
                    new()
                    {
                        UserId = MentionedUserId,
                        ScopeType = NotificationScope.Channel,
                        ScopeId = ChannelId,
                        Level = NotificationLevel.Mentions,
                        SuppressEveryone = false,
                    },
                }
            );

        await sut.CreateMentionNotificationsAsync(
            new List<long> { MentionedUserId },
            ActorId,
            GuildId,
            ChannelId,
            MessageId,
            CreatedAt,
            everyoneOriginIds: new List<long> { MentionedUserId }
        );

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Once);
    }

    // ------------------------------------------------------------------
    // "all"-level producer: per-message notifications for users who opted the
    // channel/guild into "all". Excludes the actor + anyone already notified
    // (mention/reply), and honours the mute/block/pref chain.
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateMessageNotificationsAsync_OptedInUser_GetsAMessageNotification()
    {
        var (sut, notifications, _, _, broadcaster, _, _, settings, _) = BuildSut();
        settings
            .Setup(s => s.GetOptedIntoAllAsync(GuildId, ChannelId))
            .ReturnsAsync(new List<long> { MentionedUserId });

        await sut.CreateMessageNotificationsAsync(
            ActorId, GuildId, ChannelId, MessageId, CreatedAt, alreadyNotifiedIds: new List<long>());

        notifications.Verify(
            n => n.AddAsync(It.Is<Notification>(x =>
                x.UserId == MentionedUserId && x.Type == "message" && x.ActorId == ActorId
                && x.ChannelId == ChannelId && x.MessageId == MessageId)),
            Times.Once);
        broadcaster.Verify(
            b => b.BroadcastNotificationReceivedAsync(
                MentionedUserId, It.IsAny<NotificationPayload>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateMessageNotificationsAsync_ExcludesActorAndAlreadyNotified()
    {
        var (sut, notifications, _, _, _, _, _, settings, _) = BuildSut();
        // Opted-in set includes the actor (self) and someone who already got a mention/reply —
        // both must be filtered so nobody is double-notified.
        settings
            .Setup(s => s.GetOptedIntoAllAsync(GuildId, ChannelId))
            .ReturnsAsync(new List<long> { ActorId, MentionedUserId, MentionedUserId2 });

        await sut.CreateMessageNotificationsAsync(
            ActorId, GuildId, ChannelId, MessageId, CreatedAt,
            alreadyNotifiedIds: new List<long> { MentionedUserId });

        // Only MentionedUserId2 survives (actor excluded, MentionedUserId already notified).
        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Once);
        notifications.Verify(
            n => n.AddAsync(It.Is<Notification>(x => x.UserId == MentionedUserId2)),
            Times.Once);
    }

    [Fact]
    public async Task CreateMessageNotificationsAsync_NoOptedInUsers_IsANoOp()
    {
        var (sut, notifications, _, _, _, _, _, settings, _) = BuildSut();
        settings
            .Setup(s => s.GetOptedIntoAllAsync(GuildId, ChannelId))
            .ReturnsAsync(new List<long>());

        await sut.CreateMessageNotificationsAsync(
            ActorId, GuildId, ChannelId, MessageId, CreatedAt, alreadyNotifiedIds: new List<long>());

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
        notifications.Verify(n => n.SaveChangesAsync(), Times.Never);
    }

    [Fact]
    public async Task CreateMessageNotificationsAsync_WhenMentionsDisabled_DoesNotCreate()
    {
        var (sut, notifications, _, _, _, preferences, _, settings, _) = BuildSut();
        settings
            .Setup(s => s.GetOptedIntoAllAsync(GuildId, ChannelId))
            .ReturnsAsync(new List<long> { MentionedUserId });
        // The master mentions switch doubles as the "all" gate.
        preferences
            .Setup(p => p.GetForUsersAsync(It.IsAny<List<long>>()))
            .ReturnsAsync(new List<NotificationPreference>
            {
                new() { UserId = MentionedUserId, MentionsEnabled = false },
            });

        await sut.CreateMessageNotificationsAsync(
            ActorId, GuildId, ChannelId, MessageId, CreatedAt, alreadyNotifiedIds: new List<long>());

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    // ------------------------------------------------------------------
    // guild_invite notification (invite-a-friend flow). GuildInvites pref +
    // mute + block chain; the row carries GuildId and no ChannelId/MessageId.
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateGuildInviteNotificationAsync_HappyPath_PersistsAndBroadcasts()
    {
        var (sut, notifications, _, _, broadcaster, _, _, _, _) = BuildSut();

        await sut.CreateGuildInviteNotificationAsync(MentionedUserId, ActorId, GuildId);

        notifications.Verify(
            n => n.AddAsync(It.Is<Notification>(x =>
                x.UserId == MentionedUserId && x.Type == "guild_invite" && x.ActorId == ActorId
                && x.GuildId == GuildId && x.ChannelId == null)),
            Times.Once);
        broadcaster.Verify(
            b => b.BroadcastNotificationReceivedAsync(
                MentionedUserId,
                It.Is<NotificationPayload>(p => p.Type == "guild_invite" && p.GuildId == GuildId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateGuildInviteNotificationAsync_WhenGuildInvitesDisabled_DoesNotCreate()
    {
        var (sut, notifications, _, _, _, preferences, _, _, _) = BuildSut();
        preferences
            .Setup(p => p.GetAsync(MentionedUserId))
            .ReturnsAsync(new NotificationPreference { UserId = MentionedUserId, GuildInvites = false });

        await sut.CreateGuildInviteNotificationAsync(MentionedUserId, ActorId, GuildId);

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task CreateGuildInviteNotificationAsync_SelfInvite_DoesNotCreate()
    {
        var (sut, notifications, _, _, _, _, _, _, _) = BuildSut();

        await sut.CreateGuildInviteNotificationAsync(ActorId, ActorId, GuildId);

        notifications.Verify(n => n.AddAsync(It.IsAny<Notification>()), Times.Never);
    }
}
