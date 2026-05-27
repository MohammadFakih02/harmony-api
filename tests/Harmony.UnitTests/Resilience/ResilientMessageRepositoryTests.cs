using FluentAssertions;
using Harmony.Core.Domain.Entities;
using Harmony.Core.Interfaces.Repositories;
using Harmony.Infrastructure.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Harmony.UnitTests.Resilience;

public class ResilientMessageRepositoryTests
{
    private readonly Mock<IMessageRepository> _innerMock;
    private readonly ResilientMessageRepository _sut;

    public ResilientMessageRepositoryTests()
    {
        _innerMock = new Mock<IMessageRepository>();
        _sut = new ResilientMessageRepository(
            _innerMock.Object,
            NullLogger<ResilientMessageRepository>.Instance
        );
    }

    [Fact]
    public async Task GetChannelMessagesAsync_ShouldSucceed_WhenInnerSucceeds()
    {
        var messages = new List<Message> { BuildMessage() };
        _innerMock
            .Setup(r =>
                r.GetChannelMessagesAsync(
                    It.IsAny<long>(),
                    It.IsAny<int>(),
                    It.IsAny<long?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(messages);

        var result = await _sut.GetChannelMessagesAsync(channelId: 1, limit: 10);

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetChannelMessagesAsync_ShouldRetry_OnTransientFailure()
    {
        var callCount = 0;
        var messages = new List<Message> { BuildMessage() };

        _innerMock
            .Setup(r =>
                r.GetChannelMessagesAsync(
                    It.IsAny<long>(),
                    It.IsAny<int>(),
                    It.IsAny<long?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() =>
            {
                callCount++;
                if (callCount < 3)
                    throw new Exception("transient scylla error");
                return Task.FromResult<IEnumerable<Message>>(messages);
            });

        var result = await _sut.GetChannelMessagesAsync(channelId: 1, limit: 10);

        result.Should().HaveCount(1);
        callCount.Should().Be(3);
    }

    [Fact]
    public async Task GetChannelMessagesAsync_ShouldThrowInvalidOperation_WhenCircuitOpens()
    {
        _innerMock
            .Setup(r =>
                r.GetChannelMessagesAsync(
                    It.IsAny<long>(),
                    It.IsAny<int>(),
                    It.IsAny<long?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new Exception("ScyllaDB down"));

        // Exhaust retries (3 per call) × calls needed to open circuit (5)
        for (int i = 0; i < 5; i++)
        {
            try
            {
                await _sut.GetChannelMessagesAsync(channelId: 1, limit: 10);
            }
            catch
            { /* expected */
            }
        }

        var act = async () => await _sut.GetChannelMessagesAsync(channelId: 1, limit: 10);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*temporarily unavailable*");
    }

    [Fact]
    public async Task SaveAsync_ShouldSucceed_WhenInnerSucceeds()
    {
        _innerMock
            .Setup(r => r.SaveAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var act = async () => await _sut.SaveAsync(BuildMessage());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteAsync_ShouldSucceed_WhenInnerSucceeds()
    {
        _innerMock
            .Setup(r =>
                r.DeleteAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>())
            )
            .Returns(Task.CompletedTask);

        var act = async () => await _sut.DeleteAsync(messageId: 1001, channelId: 1);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotFound()
    {
        _innerMock
            .Setup(r => r.GetByIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Message?)null);

        var result = await _sut.GetByIdAsync(messageId: 9999);

        result.Should().BeNull();
    }

    // --- Helpers ---

    private static Message BuildMessage() =>
        new()
        {
            MessageId = 1001,
            ChannelId = 1,
            UserId = 99,
            Content = "hello",
            AttachmentIds = [],
            MentionIds = [],
            IsDeleted = false,
            IsEdited = false,
            MessageType = "text",
        };
}
