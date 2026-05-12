using System;
using System.Threading;

namespace Harmony.Core.Services;

/// <summary>
/// Generates 64-bit Snowflake IDs.
///
/// Bit layout (64 bits total):
///   [63]       Sign bit — always 0 (keeps IDs positive as long)
///   [62..22]   41 bits — milliseconds since HarmonyEpoch (~69 year range)
///   [21..17]    5 bits — datacenter ID (0–31)
///   [16..12]    5 bits — worker ID (0–31)
///   [11..0]    12 bits — sequence number per millisecond (0–4095)
///
/// Max throughput: 4,096 IDs/ms per worker = ~4 million IDs/sec cluster-wide (32 workers).
/// IDs are time-ordered and sortable as bigint in PostgreSQL and ScyllaDB.
/// </summary>
public sealed class SnowflakeIdGenerator : ISnowflakeIdGenerator
{
    // 2024-01-01 00:00:00.000 UTC — Harmony's custom epoch
    // Never change this after IDs are in production.
    public static readonly DateTimeOffset HarmonyEpoch =
        new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly long EpochMs = HarmonyEpoch.ToUnixTimeMilliseconds();

    // Bit widths
    private const int WorkerIdBits     = 5;
    private const int DatacenterIdBits = 5;
    private const int SequenceBits     = 12;

    // Max values (inclusive)
    private const long MaxWorkerId     = (1L << WorkerIdBits) - 1;     // 31
    private const long MaxDatacenterId = (1L << DatacenterIdBits) - 1; // 31
    private const long MaxSequence     = (1L << SequenceBits) - 1;     // 4095

    // Bit shift offsets
    private const int WorkerIdShift     = SequenceBits;                              // 12
    private const int DatacenterIdShift = SequenceBits + WorkerIdBits;               // 17
    private const int TimestampShift    = SequenceBits + WorkerIdBits + DatacenterIdBits; // 22

    private readonly long _workerId;
    private readonly long _datacenterId;

    private long _lastTimestamp = -1L;
    private long _sequence = 0L;

    private readonly object _lock = new();

    /// <param name="workerId">0–31. Unique per process instance. Read from config in production.</param>
    /// <param name="datacenterId">0–31. Unique per datacenter/region. Read from config in production.</param>
    public SnowflakeIdGenerator(long workerId = 0, long datacenterId = 0)
    {
        if (workerId < 0 || workerId > MaxWorkerId)
            throw new ArgumentOutOfRangeException(nameof(workerId),
                $"Worker ID must be between 0 and {MaxWorkerId}.");

        if (datacenterId < 0 || datacenterId > MaxDatacenterId)
            throw new ArgumentOutOfRangeException(nameof(datacenterId),
                $"Datacenter ID must be between 0 and {MaxDatacenterId}.");

        _workerId = workerId;
        _datacenterId = datacenterId;
    }

    /// <summary>
    /// Generates the next unique Snowflake ID. Thread-safe.
    /// </summary>
    public long NextId()
    {
        lock (_lock)
        {
            var timestamp = CurrentMs();

            if (timestamp < _lastTimestamp)
            {
                // Clock moved backward — this can happen after NTP corrections.
                // Wait until we catch up rather than generating a duplicate.
                var drift = _lastTimestamp - timestamp;
                if (drift > 5)
                    throw new InvalidOperationException(
                        $"Clock moved backward by {drift}ms. Refusing to generate ID to prevent duplicates.");

                // For small drifts (≤5ms), spin-wait until the clock catches up.
                timestamp = WaitForNextMs(_lastTimestamp);
            }

            if (timestamp == _lastTimestamp)
            {
                _sequence = (_sequence + 1) & MaxSequence;
                if (_sequence == 0)
                {
                    // Sequence exhausted for this millisecond — wait for the next one.
                    timestamp = WaitForNextMs(_lastTimestamp);
                }
            }
            else
            {
                _sequence = 0L;
            }

            _lastTimestamp = timestamp;

            return ((timestamp - EpochMs) << TimestampShift)
                 | (_datacenterId << DatacenterIdShift)
                 | (_workerId << WorkerIdShift)
                 | _sequence;
        }
    }

    /// <summary>
    /// Extracts the UTC timestamp embedded in a Snowflake ID.
    /// Useful for debugging and audit log display.
    /// </summary>
    public static DateTimeOffset ExtractTimestamp(long snowflakeId)
    {
        var ms = (snowflakeId >> TimestampShift) + EpochMs;
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }

    /// <summary>
    /// Extracts the worker ID embedded in a Snowflake ID.
    /// </summary>
    public static int ExtractWorkerId(long snowflakeId) =>
        (int)((snowflakeId >> WorkerIdShift) & MaxWorkerId);

    /// <summary>
    /// Extracts the datacenter ID embedded in a Snowflake ID.
    /// </summary>
    public static int ExtractDatacenterId(long snowflakeId) =>
        (int)((snowflakeId >> DatacenterIdShift) & MaxDatacenterId);

    /// <summary>
    /// Extracts the sequence number embedded in a Snowflake ID.
    /// </summary>
    public static int ExtractSequence(long snowflakeId) =>
        (int)(snowflakeId & MaxSequence);

    // Returns the current Unix time in milliseconds.
    private static long CurrentMs() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // Spin-wait until the system clock has advanced past lastTimestamp.
    private static long WaitForNextMs(long lastTimestamp)
    {
        long ts;
        do { ts = CurrentMs(); } while (ts <= lastTimestamp);
        return ts;
    }
}
