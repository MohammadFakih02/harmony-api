using System.Buffers;
using FluentAssertions;
using Harmony.API.SignalR;
using MessagePack;
using MessagePack.Resolvers;

namespace Harmony.IntegrationTests.SignalR;

/// <summary>
/// D5 — pins the MessagePack hub protocol's wire contract. Two invisible-in-a-build failure modes are
/// covered: (1) a precision bug would silently corrupt Snowflake IDs — 64-bit IDs must serialize as
/// MessagePack <em>strings</em> (not native int64, which the JS client decodes to a lossy float64) and
/// round-trip exactly above 2^53; (2) a casing mismatch would make every field <c>undefined</c> on the
/// JS client — Harmony DTOs must serialize with <b>camelCase</b> keys, matching the JSON protocol.
/// </summary>
public class MessagePackLongAsStringResolverTests
{
    // Long/long? formatter in isolation (no DTO layer).
    private static readonly MessagePackSerializerOptions LongOptions = MessagePackSerializerOptions
        .Standard.WithResolver(
            CompositeResolver.Create(
                MessagePackLongAsStringResolver.Instance,
                ContractlessStandardResolver.Instance
            )
        )
        .WithSecurity(MessagePackSecurity.UntrustedData);

    // The exact composite the SignalR MessagePack protocol is configured with in DependencyInjection.
    private static readonly MessagePackSerializerOptions ProdOptions = MessagePackSerializerOptions
        .Standard.WithResolver(
            CompositeResolver.Create(
                MessagePackLongAsStringResolver.Instance,
                MessagePackCamelCaseResolver.Instance,
                ContractlessStandardResolver.Instance
            )
        )
        .WithSecurity(MessagePackSecurity.UntrustedData);

    // 2^53 + 1 — the smallest integer a float64 cannot represent exactly. Native int64 + the JS client
    // would return 9_007_199_254_740_992; a string keeps it exact.
    private const long UnsafeSnowflake = 9_007_199_254_740_993L;

    // ---- long / long? formatter ---------------------------------------------

    [Fact]
    public void Long_SerializesAsMessagePackString()
    {
        var bytes = MessagePackSerializer.Serialize(UnsafeSnowflake, LongOptions);
        new MessagePackReader(bytes).NextMessagePackType.Should().Be(MessagePackType.String);
    }

    [Fact]
    public void Long_RoundTripsExactly_AboveFloat64Precision()
    {
        var bytes = MessagePackSerializer.Serialize(UnsafeSnowflake, LongOptions);
        MessagePackSerializer.Deserialize<long>(bytes, LongOptions).Should().Be(UnsafeSnowflake);
    }

    [Fact]
    public void Long_ToleratesNativeIntOnRead()
    {
        // A client that sent a native int (not our string form) must still deserialize.
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteInt64(1234567890123456789L);
        writer.Flush();

        MessagePackSerializer
            .Deserialize<long>(buffer.WrittenMemory, LongOptions)
            .Should()
            .Be(1234567890123456789L);
    }

    [Fact]
    public void NullableLong_Value_SerializesAsString_AndRoundTrips()
    {
        long? value = UnsafeSnowflake;
        var bytes = MessagePackSerializer.Serialize(value, LongOptions);

        new MessagePackReader(bytes).NextMessagePackType.Should().Be(MessagePackType.String);
        MessagePackSerializer.Deserialize<long?>(bytes, LongOptions).Should().Be(UnsafeSnowflake);
    }

    [Fact]
    public void NullableLong_Null_SerializesAsNil_AndRoundTrips()
    {
        long? value = null;
        var bytes = MessagePackSerializer.Serialize(value, LongOptions);

        new MessagePackReader(bytes).NextMessagePackType.Should().Be(MessagePackType.Nil);
        MessagePackSerializer.Deserialize<long?>(bytes, LongOptions).Should().BeNull();
    }

    // ---- DTO serialization (production composite) ---------------------------

    [Fact]
    public void Dto_SerializesWith_CamelCaseKeys_And_StringIds()
    {
        var dto = new SamplePayload(
            Id: UnsafeSnowflake,
            GuildId: null,
            Content: "hi",
            ReplyToId: UnsafeSnowflake - 1
        );

        var bytes = MessagePackSerializer.Serialize(dto, ProdOptions);

        // Read the map literally (what the JS client sees) — camelCase keys, id as an exact string.
        var reader = new MessagePackReader(bytes);
        var count = reader.ReadMapHeader();
        var keys = new List<string>();
        string? idValue = null;
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadString()!;
            keys.Add(key);
            if (key == "id")
                idValue = reader.ReadString();
            else
                reader.Skip();
        }

        keys.Should().Contain(["id", "guildId", "content", "replyToId"]);
        keys.Should().NotContain("Id"); // never the raw PascalCase member name
        idValue.Should().Be(UnsafeSnowflake.ToString()); // string, no precision loss
    }

    [Fact]
    public void Dto_Deserialize_IsGuarded_AsBroadcastOnly()
    {
        var bytes = MessagePackSerializer.Serialize(
            new SamplePayload(1, null, "x", null),
            ProdOptions
        );

        var act = () => MessagePackSerializer.Deserialize<SamplePayload>(bytes, ProdOptions);

        act.Should().Throw<Exception>(); // NotSupportedException, wrapped by MessagePack
    }

    // Attribute-less record — resolved by MessagePackCamelCaseResolver (its namespace starts with
    // "Harmony"), longs by the long-as-string formatter. Public so MessagePack reflection sees it.
    public sealed record SamplePayload(long Id, long? GuildId, string Content, long? ReplyToId);
}
