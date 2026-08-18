using System.Globalization;
using MessagePack;
using MessagePack.Formatters;

namespace Harmony.API.SignalR;

/// <summary>
/// A MessagePack <see cref="IFormatterResolver"/> (Track D5) that serializes every <see cref="long"/>
/// and <c>long?</c> — Snowflake IDs — as a MessagePack <b>string</b> instead of a native int64, and
/// reads back either form. It is composed <em>ahead of</em> <c>ContractlessStandardResolver</c> so it
/// wins for long/long? while every attribute-less hub DTO is still serialized by name. The result:
/// the MessagePack and JSON hub protocols share ONE wire contract — IDs are always strings on the wire
/// (the binary mirror of the JSON <c>LongStringConverter</c>).
///
/// <para><b>Why strings:</b> MessagePack-CSharp encodes <c>long</c> as a native 64-bit int, but the
/// JavaScript <c>@microsoft/signalr-protocol-msgpack</c> client decodes a 64-bit int into a float64 JS
/// <c>number</c> — silently losing precision above 2^53 — and exposes no option to change it. The whole
/// frontend already treats Snowflake IDs as strings (compared as BigInt), so emitting them as strings
/// keeps every id exact and leaves the client code unchanged.</para>
/// </summary>
public sealed class MessagePackLongAsStringResolver : IFormatterResolver
{
    public static readonly MessagePackLongAsStringResolver Instance = new();

    private static readonly IMessagePackFormatter<long> LongFormatter = new LongAsStringFormatter();
    private static readonly IMessagePackFormatter<long?> NullableLongFormatter =
        new NullableLongAsStringFormatter();

    private MessagePackLongAsStringResolver() { }

    public IMessagePackFormatter<T>? GetFormatter<T>()
    {
        if (typeof(T) == typeof(long))
            return (IMessagePackFormatter<T>)(object)LongFormatter;
        if (typeof(T) == typeof(long?))
            return (IMessagePackFormatter<T>)(object)NullableLongFormatter;
        return null; // defer to the composed contractless resolver for every other type
    }

    private sealed class LongAsStringFormatter : IMessagePackFormatter<long>
    {
        public void Serialize(ref MessagePackWriter writer, long value, MessagePackSerializerOptions options) =>
            writer.Write(value.ToString(CultureInfo.InvariantCulture));

        public long Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
            // Emit strings, but tolerate a client that sent a native int for the same field.
            reader.NextMessagePackType == MessagePackType.String
                ? long.Parse(reader.ReadString()!, CultureInfo.InvariantCulture)
                : reader.ReadInt64();
    }

    private sealed class NullableLongAsStringFormatter : IMessagePackFormatter<long?>
    {
        public void Serialize(ref MessagePackWriter writer, long? value, MessagePackSerializerOptions options)
        {
            if (value is null)
                writer.WriteNil();
            else
                writer.Write(value.Value.ToString(CultureInfo.InvariantCulture));
        }

        public long? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil())
                return null;
            if (reader.NextMessagePackType == MessagePackType.String)
            {
                var s = reader.ReadString();
                return string.IsNullOrEmpty(s) ? null : long.Parse(s, CultureInfo.InvariantCulture);
            }
            return reader.ReadInt64();
        }
    }
}
