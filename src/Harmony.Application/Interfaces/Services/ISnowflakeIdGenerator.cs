namespace Harmony.Application.Services;

/// <summary>
/// Generates unique, time-ordered 64-bit Snowflake IDs.
/// Register as a singleton in DI — the internal sequence counter must be shared.
/// </summary>
public interface ISnowflakeIdGenerator
{
    /// <summary>
    /// Returns the next unique Snowflake ID. Thread-safe.
    /// </summary>
    long NextId();
}
