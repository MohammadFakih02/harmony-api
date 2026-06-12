using Cassandra;
using FluentAssertions;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Scylla.Repositories;
using Moq;
using Polly;
using Polly.CircuitBreaker;

namespace Harmony.UnitTests.Resiliency;

public class ResilientMessageRepositoryTests
{
    private static ResiliencePipeline BuildTestPipeline(int minimumThroughput = 2) =>
        new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<NoHostAvailableException>(),
                FailureRatio = 1.0,
                MinimumThroughput = minimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(60),
                BreakDuration = TimeSpan.FromSeconds(60),
            })
            .Build();

    [Fact]
    public async Task GetChannelMessages_WhenInnerSucceeds_ReturnsMessages()
    {
        var expected = new List<Message> { new() { MessageId = 1 } };
        var inner = new Mock<IMessageRepository>();
        inner
            .Setup(r => r.GetChannelMessagesAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var sut = new ResilientMessageRepository(inner.Object, BuildTestPipeline());

        var result = await sut.GetChannelMessagesAsync(channelId: 1);

        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetChannelMessages_WhenCircuitTrips_ThrowsBrokenCircuitException()
    {
        var inner = new Mock<IMessageRepository>();
        inner
            .Setup(r => r.GetChannelMessagesAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NoHostAvailableException(new Dictionary<System.Net.IPEndPoint, Exception>()));

        var sut = new ResilientMessageRepository(inner.Object, BuildTestPipeline(minimumThroughput: 2));

        // First two calls: inner throws NoHostAvailableException (counted as failures).
        for (var i = 0; i < 2; i++)
        {
            await sut.Invoking(s => s.GetChannelMessagesAsync(channelId: 1))
                .Should().ThrowAsync<NoHostAvailableException>();
        }

        // Third call: circuit is now open — Polly throws BrokenCircuitException without calling inner.
        await sut.Invoking(s => s.GetChannelMessagesAsync(channelId: 1))
            .Should().ThrowAsync<BrokenCircuitException>();
    }

    [Fact]
    public async Task SaveAsync_IsNotWrapped_DelegatesDirectlyEvenWhenCircuitOpen()
    {
        var inner = new Mock<IMessageRepository>();
        inner
            .Setup(r => r.GetChannelMessagesAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NoHostAvailableException(new Dictionary<System.Net.IPEndPoint, Exception>()));
        inner
            .Setup(r => r.SaveAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new ResilientMessageRepository(inner.Object, BuildTestPipeline(minimumThroughput: 2));

        // Trip the circuit open.
        for (var i = 0; i < 2; i++)
        {
            await sut.Invoking(s => s.GetChannelMessagesAsync(channelId: 1))
                .Should().ThrowAsync<NoHostAvailableException>();
        }
        await sut.Invoking(s => s.GetChannelMessagesAsync(channelId: 1))
            .Should().ThrowAsync<BrokenCircuitException>();

        // SaveAsync must still succeed — writes are not wrapped in the pipeline.
        await sut.SaveAsync(new Message { MessageId = 42 });

        inner.Verify(r => r.SaveAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
