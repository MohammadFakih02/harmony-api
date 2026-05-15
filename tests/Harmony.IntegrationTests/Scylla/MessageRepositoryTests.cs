using Cassandra;
using FluentAssertions;
using Harmony.Core.Domain.Entities;
using Harmony.Infrastructure.Scylla.Repositories;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harmony.IntegrationTests.Scylla;

public class MessageRepositoryTests : ScyllaTestBase
{
    protected override IEnumerable<string> TablesToTruncate =>
        ["messages_by_channel", "messages_by_id", "pinned_messages"];

    private MessageRepository CreateRepository()
    {
        var stub = new ScyllaSessionFactoryStub(Session);
        return new MessageRepository(stub, NullLogger<MessageRepository>.Instance);
    }

    // --- SaveAsync + GetChannelMessagesAsync ---

    [Fact]
    public async Task SaveAsync_ShouldPersistMessage_AndBeRetrievableByChannel()
    {
        var repo = CreateRepository();
        var message = BuildMessage(channelId: 1, messageId: 1001, content: "hello world");

        // Check what keyspace the session is on
        var keyspace = Session.Keyspace;
        keyspace.Should().Be("harmony_test");

        await repo.SaveAsync(message);
        await Task.Delay(500);

        var results = await repo.GetChannelMessagesAsync(channelId: 1, limit: 10);
        results.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetChannelMessagesAsync_ShouldReturnNewestFirst()
    {
        var repo = CreateRepository();

        await repo.SaveAsync(BuildMessage(channelId: 2, messageId: 1000));
        await repo.SaveAsync(BuildMessage(channelId: 2, messageId: 2000));
        await repo.SaveAsync(BuildMessage(channelId: 2, messageId: 3000));

        var results = (await repo.GetChannelMessagesAsync(channelId: 2, limit: 10)).ToList();

        results.Should().HaveCount(3);
        results[0].MessageId.Should().Be(3000);
        results[1].MessageId.Should().Be(2000);
        results[2].MessageId.Should().Be(1000);
    }

    [Fact]
    public async Task GetChannelMessagesAsync_ShouldRespectLimit()
    {
        var repo = CreateRepository();

        foreach (var id in new[] { 1000L, 2000L, 3000L, 4000L, 5000L })
            await repo.SaveAsync(BuildMessage(channelId: 3, messageId: id));

        var results = await repo.GetChannelMessagesAsync(channelId: 3, limit: 3);

        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetChannelMessagesAsync_WithBeforeMessageId_ShouldPageCorrectly()
    {
        var repo = CreateRepository();

        foreach (var id in new[] { 1000L, 2000L, 3000L, 4000L, 5000L })
            await repo.SaveAsync(BuildMessage(channelId: 4, messageId: id));

        // Get page 1 — newest 3
        var page1 = (await repo.GetChannelMessagesAsync(channelId: 4, limit: 3)).ToList();
        page1.Should().HaveCount(3);
        page1[0].MessageId.Should().Be(5000);

        // Get page 2 — before the oldest in page 1
        var page2 = (
            await repo.GetChannelMessagesAsync(
                channelId: 4,
                limit: 3,
                beforeMessageId: page1.Last().MessageId
            )
        ).ToList();
        page2.Should().HaveCount(2);
        page2[0].MessageId.Should().Be(2000);
        page2[1].MessageId.Should().Be(1000);
    }

    [Fact]
    public async Task GetChannelMessagesAsync_EmptyChannel_ShouldReturnEmpty()
    {
        var repo = CreateRepository();

        var results = await repo.GetChannelMessagesAsync(channelId: 999, limit: 10);

        results.Should().BeEmpty();
    }

    // --- GetByIdAsync ---

    [Fact]
    public async Task GetByIdAsync_ShouldReturnMessage_WhenExists()
    {
        var repo = CreateRepository();
        var message = BuildMessage(channelId: 5, messageId: 5001, content: "find me");

        await repo.SaveAsync(message);

        var result = await repo.GetByIdAsync(5001);
        result.Should().NotBeNull();
        result!.MessageId.Should().Be(5001);
        result.Content.Should().Be("find me");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotExists()
    {
        var repo = CreateRepository();

        var result = await repo.GetByIdAsync(99999);

        result.Should().BeNull();
    }

    // --- DeleteAsync ---

    [Fact]
    public async Task DeleteAsync_ShouldSoftDelete_InBothTables()
    {
        var repo = CreateRepository();
        var message = BuildMessage(channelId: 6, messageId: 6001);

        await repo.SaveAsync(message);
        await repo.DeleteAsync(messageId: 6001, channelId: 6);

        // Check messages_by_channel
        var byChannel = (await repo.GetChannelMessagesAsync(channelId: 6, limit: 10)).First();
        byChannel.IsDeleted.Should().BeTrue();

        // Check messages_by_id
        var byId = await repo.GetByIdAsync(6001);
        byId!.IsDeleted.Should().BeTrue();
    }

    // --- EditAsync ---

    [Fact]
    public async Task EditAsync_ShouldUpdateContent_AndSetIsEdited()
    {
        var repo = CreateRepository();
        var message = BuildMessage(channelId: 7, messageId: 7001, content: "original");

        await repo.SaveAsync(message);
        await repo.EditAsync(messageId: 7001, channelId: 7, newContent: "edited");

        var byChannel = (await repo.GetChannelMessagesAsync(channelId: 7, limit: 10)).First();
        byChannel.Content.Should().Be("edited");
        byChannel.IsEdited.Should().BeTrue();
        byChannel.EditedAt.Should().NotBeNull();

        var byId = await repo.GetByIdAsync(7001);
        byId!.Content.Should().Be("edited");
        byId.IsEdited.Should().BeTrue();
    }

    // --- PinAsync + GetPinnedAsync + UnpinAsync ---

    [Fact]
    public async Task PinAsync_ShouldPersistPinnedMessage()
    {
        var repo = CreateRepository();

        await repo.PinAsync(channelId: 8, messageId: 8001, pinnedBy: 42);

        var pinned = (await repo.GetPinnedAsync(channelId: 8)).ToList();
        pinned.Should().HaveCount(1);
        pinned[0].MessageId.Should().Be(8001);
        pinned[0].PinnedBy.Should().Be(42);
    }

    [Fact]
    public async Task UnpinAsync_ShouldRemovePinnedMessage()
    {
        var repo = CreateRepository();

        await repo.PinAsync(channelId: 9, messageId: 9001, pinnedBy: 42);
        await repo.UnpinAsync(channelId: 9, pinnedAt: 9001);

        var pinned = await repo.GetPinnedAsync(channelId: 9);
        pinned.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPinnedAsync_ShouldReturnNewestFirst()
    {
        var repo = CreateRepository();

        await repo.PinAsync(channelId: 10, messageId: 1000, pinnedBy: 1);
        await repo.PinAsync(channelId: 10, messageId: 2000, pinnedBy: 1);
        await repo.PinAsync(channelId: 10, messageId: 3000, pinnedBy: 1);

        var pinned = (await repo.GetPinnedAsync(channelId: 10)).ToList();
        pinned[0].MessageId.Should().Be(3000);
        pinned[1].MessageId.Should().Be(2000);
        pinned[2].MessageId.Should().Be(1000);
    }

    // --- Helpers ---

    private static Message BuildMessage(
        long channelId,
        long messageId,
        string content = "test content"
    ) =>
        new()
        {
            ChannelId = channelId,
            MessageId = messageId,
            UserId = 99,
            Content = content,
            AttachmentIds = [],
            MentionIds = [],
            ReplyToId = null,
            IsDeleted = false,
            IsEdited = false,
            EditedAt = null,
            MessageType = "text",
        };
}
