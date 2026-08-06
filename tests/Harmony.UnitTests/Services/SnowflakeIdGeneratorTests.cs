using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Harmony.Application.Services;
using Xunit;

namespace Harmony.UnitTests.Services;

public class SnowflakeIdGeneratorTests
{
    private readonly SnowflakeIdGenerator _generator = new(workerId: 1, datacenterId: 1);

    [Fact]
    public void NextId_ReturnsPositiveLong()
    {
        _generator.NextId().Should().BePositive();
    }

    [Fact]
    public void NextId_IdsAreMonotonicallyIncreasing()
    {
        var ids = new long[1000];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = _generator.NextId();

        for (var i = 1; i < ids.Length; i++)
            ids[i].Should().BeGreaterThan(ids[i - 1]);
    }

    [Fact]
    public void NextId_IdsAreUniqueUnderConcurrentLoad()
    {
        var bag = new System.Collections.Concurrent.ConcurrentBag<long>();
        var threads = new Thread[8];

        for (var t = 0; t < threads.Length; t++)
        {
            threads[t] = new Thread(() =>
            {
                for (var i = 0; i < 500; i++)
                    bag.Add(_generator.NextId());
            });
        }

        foreach (var th in threads) th.Start();
        foreach (var th in threads) th.Join();

        bag.Should().OnlyHaveUniqueItems();
        bag.Should().HaveCount(4000);
    }

    [Fact]
    public void NextId_EmbeddedTimestampIsRecentUtc()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var id = _generator.NextId();
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        var embedded = SnowflakeIdGenerator.ExtractTimestamp(id);
        embedded.Should().BeOnOrAfter(before);
        embedded.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void NextId_EmbeddedWorkerAndDatacenterIds()
    {
        var gen = new SnowflakeIdGenerator(workerId: 7, datacenterId: 3);
        var id = gen.NextId();

        SnowflakeIdGenerator.ExtractWorkerId(id).Should().Be(7);
        SnowflakeIdGenerator.ExtractDatacenterId(id).Should().Be(3);
    }

    [Fact]
    public void NextId_SequenceResetsBetweenMilliseconds()
    {
        // Generate IDs until we cross a millisecond boundary, then check sequence resets.
        // We can't reliably observe this without mocking the clock, but we can at least
        // confirm that ids across a known gap have sequence 0 in the second batch.
        var id1 = _generator.NextId();
        Thread.Sleep(5); // guaranteed new millisecond
        var id2 = _generator.NextId();

        SnowflakeIdGenerator.ExtractSequence(id2).Should().Be(0);
        id2.Should().BeGreaterThan(id1);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(32, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 32)]
    public void Constructor_ThrowsOnInvalidIds(long workerId, long datacenterId)
    {
        var act = () => new SnowflakeIdGenerator(workerId, datacenterId);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ExtractTimestamp_RoundtripsEpoch()
    {
        // An ID of 0 should extract to exactly the Harmony epoch.
        var ts = SnowflakeIdGenerator.ExtractTimestamp(0L);
        ts.Should().Be(SnowflakeIdGenerator.HarmonyEpoch);
    }

    [Fact]
    public void MaxWorkerId_Is31()
    {
        // This would throw if 31 is out of range, confirming 5-bit field.
        var act = () => new SnowflakeIdGenerator(workerId: 31);
        act.Should().NotThrow();
    }

    [Fact]
    public void MaxDatacenterId_Is31()
    {
        var act = () => new SnowflakeIdGenerator(datacenterId: 31);
        act.Should().NotThrow();
    }
}
