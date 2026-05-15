using FluentAssertions;
using Harmony.Infrastructure.Scylla.Repositories;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harmony.IntegrationTests.Scylla;

public class ReadStateRepositoryTests : ScyllaTestBase
{
    protected override IEnumerable<string> TablesToTruncate => ["read_states"];

    private ReadStateRepository CreateRepository()
    {
        var stub = new ScyllaSessionFactoryStub(Session);
        return new ReadStateRepository(stub, NullLogger<ReadStateRepository>.Instance);
    }

    // --- MarkAsReadAsync + GetLastReadMessageIdAsync ---

    [Fact]
    public async Task MarkAsReadAsync_ShouldPersistLastReadMessageId()
    {
        var repo = CreateRepository();

        await repo.MarkAsReadAsync(userId: 1, channelId: 100, lastReadMessageId: 5000);

        var result = await repo.GetLastReadMessageIdAsync(userId: 1, channelId: 100);
        result.Should().Be(5000);
    }

    [Fact]
    public async Task MarkAsReadAsync_ShouldOverwrite_WhenCalledAgain()
    {
        var repo = CreateRepository();

        await repo.MarkAsReadAsync(userId: 2, channelId: 200, lastReadMessageId: 1000);
        await repo.MarkAsReadAsync(userId: 2, channelId: 200, lastReadMessageId: 9999);

        var result = await repo.GetLastReadMessageIdAsync(userId: 2, channelId: 200);
        result.Should().Be(9999);
    }

    [Fact]
    public async Task GetLastReadMessageIdAsync_ShouldReturnNull_WhenNeverRead()
    {
        var repo = CreateRepository();

        var result = await repo.GetLastReadMessageIdAsync(userId: 99, channelId: 99);

        result.Should().BeNull();
    }

    [Fact]
    public async Task MarkAsReadAsync_ShouldBeIndependent_PerChannel()
    {
        var repo = CreateRepository();

        await repo.MarkAsReadAsync(userId: 3, channelId: 301, lastReadMessageId: 1000);
        await repo.MarkAsReadAsync(userId: 3, channelId: 302, lastReadMessageId: 2000);
        await repo.MarkAsReadAsync(userId: 3, channelId: 303, lastReadMessageId: 3000);

        var ch1 = await repo.GetLastReadMessageIdAsync(userId: 3, channelId: 301);
        var ch2 = await repo.GetLastReadMessageIdAsync(userId: 3, channelId: 302);
        var ch3 = await repo.GetLastReadMessageIdAsync(userId: 3, channelId: 303);

        ch1.Should().Be(1000);
        ch2.Should().Be(2000);
        ch3.Should().Be(3000);
    }

    [Fact]
    public async Task MarkAsReadAsync_ShouldBeIndependent_PerUser()
    {
        var repo = CreateRepository();

        await repo.MarkAsReadAsync(userId: 10, channelId: 400, lastReadMessageId: 1000);
        await repo.MarkAsReadAsync(userId: 11, channelId: 400, lastReadMessageId: 2000);
        await repo.MarkAsReadAsync(userId: 12, channelId: 400, lastReadMessageId: 3000);

        var u1 = await repo.GetLastReadMessageIdAsync(userId: 10, channelId: 400);
        var u2 = await repo.GetLastReadMessageIdAsync(userId: 11, channelId: 400);
        var u3 = await repo.GetLastReadMessageIdAsync(userId: 12, channelId: 400);

        u1.Should().Be(1000);
        u2.Should().Be(2000);
        u3.Should().Be(3000);
    }

    // --- GetAllForUserAsync ---

    [Fact]
    public async Task GetAllForUserAsync_ShouldReturnAllChannels_ForUser()
    {
        var repo = CreateRepository();

        await repo.MarkAsReadAsync(userId: 20, channelId: 501, lastReadMessageId: 100);
        await repo.MarkAsReadAsync(userId: 20, channelId: 502, lastReadMessageId: 200);
        await repo.MarkAsReadAsync(userId: 20, channelId: 503, lastReadMessageId: 300);

        var result = await repo.GetAllForUserAsync(userId: 20, channelIds: [501, 502, 503]);

        result.Should().HaveCount(3);
        result[501].Should().Be(100);
        result[502].Should().Be(200);
        result[503].Should().Be(300);
    }

    [Fact]
    public async Task GetAllForUserAsync_ShouldExcludeChannels_NeverRead()
    {
        var repo = CreateRepository();

        await repo.MarkAsReadAsync(userId: 21, channelId: 601, lastReadMessageId: 100);

        // Pass 601 (read) and 602 (never read)
        var result = await repo.GetAllForUserAsync(userId: 21, channelIds: [601, 602]);

        result.Should().HaveCount(1);
        result.Should().ContainKey(601);
        result.Should().NotContainKey(602);
    }

    [Fact]
    public async Task GetAllForUserAsync_ShouldReturnEmpty_WhenNoChannelsRead()
    {
        var repo = CreateRepository();

        var result = await repo.GetAllForUserAsync(userId: 99, channelIds: [701, 702, 703]);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllForUserAsync_ShouldNotReturnOtherUsers_ReadStates()
    {
        var repo = CreateRepository();

        await repo.MarkAsReadAsync(userId: 30, channelId: 800, lastReadMessageId: 500);
        await repo.MarkAsReadAsync(userId: 31, channelId: 800, lastReadMessageId: 999);

        // Query for user 30 only
        var result = await repo.GetAllForUserAsync(userId: 30, channelIds: [800]);

        result.Should().HaveCount(1);
        result[800].Should().Be(500);
    }
}
