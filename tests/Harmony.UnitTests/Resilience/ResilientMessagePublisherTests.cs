using System.Net.Sockets; // Added for SocketException
using FluentAssertions;
using Harmony.Core.Exceptions;
using Harmony.Core.Interfaces;
using Harmony.Infrastructure.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Harmony.UnitTests.Resilience;

public class ResilientMessagePublisherTests
{
    private readonly Mock<IMessagePublisher> _innerMock;
    private readonly ResilientMessagePublisher _sut;

    public ResilientMessagePublisherTests()
    {
        _innerMock = new Mock<IMessagePublisher>();

        var policyProvider = new RabbitMQPolicyProvider(
            NullLogger<RabbitMQPolicyProvider>.Instance
        );

        _sut = new ResilientMessagePublisher(
            _innerMock.Object,
            policyProvider,
            NullLogger<ResilientMessagePublisher>.Instance
        );
    }

    [Fact]
    public async Task PublishMessageSentAsync_ShouldSucceed_WhenInnerSucceeds()
    {
        _innerMock
            .Setup(p =>
                p.PublishMessageSentAsync(
                    It.IsAny<MessageSentEvent>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);

        var act = async () => await _sut.PublishMessageSentAsync(BuildMessageSentEvent());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PublishMessageSentAsync_ShouldThrowServiceUnavailable_WhenCircuitOpens()
    {
        // Fail 3 times throwing a SocketException to open the circuit
        _innerMock
            .Setup(p =>
                p.PublishMessageSentAsync(
                    It.IsAny<MessageSentEvent>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new SocketException()); // Throws realistic connection-level socket failure

        // Exhaust the allowed failures to open the circuit
        for (int i = 0; i < 3; i++)
        {
            try
            {
                await _sut.PublishMessageSentAsync(BuildMessageSentEvent());
            }
            catch
            { /* expected */
            }
        }

        // Next call should hit open circuit and throw ServiceUnavailableException
        var act = async () => await _sut.PublishMessageSentAsync(BuildMessageSentEvent());

        await act.Should()
            .ThrowAsync<ServiceUnavailableException>()
            .WithMessage("*temporarily unavailable*");
    }

    [Fact]
    public async Task PublishMessageDeletedAsync_ShouldSucceed_WhenInnerSucceeds()
    {
        _innerMock
            .Setup(p =>
                p.PublishMessageDeletedAsync(
                    It.IsAny<MessageDeletedEvent>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);

        var act = async () => await _sut.PublishMessageDeletedAsync(BuildMessageDeletedEvent());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PublishMessageEditedAsync_ShouldSucceed_WhenInnerSucceeds()
    {
        _innerMock
            .Setup(p =>
                p.PublishMessageEditedAsync(
                    It.IsAny<MessageEditedEvent>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);

        var act = async () => await _sut.PublishMessageEditedAsync(BuildMessageEditedEvent());

        await act.Should().NotThrowAsync();
    }

    // --- Helpers ---

    private static MessageSentEvent BuildMessageSentEvent() =>
        new(
            MessageId: 1001,
            ChannelId: 1,
            GuildId: 1,
            UserId: 99,
            Content: "hello",
            MessageType: "text",
            AttachmentIds: [],
            MentionIds: [],
            ReplyToId: null,
            SentAt: DateTimeOffset.UtcNow
        );

    private static MessageDeletedEvent BuildMessageDeletedEvent() =>
        new(
            MessageId: 1001,
            ChannelId: 1,
            GuildId: 1,
            DeletedByUserId: 99,
            DeletedAt: DateTimeOffset.UtcNow
        );

    private static MessageEditedEvent BuildMessageEditedEvent() =>
        new(
            MessageId: 1001,
            ChannelId: 1,
            GuildId: 1,
            EditedByUserId: 99,
            NewContent: "edited",
            EditedAt: DateTimeOffset.UtcNow
        );
}
