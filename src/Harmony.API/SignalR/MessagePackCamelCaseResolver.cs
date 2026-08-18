using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;
using MessagePack.Formatters;

namespace Harmony.API.SignalR;

/// <summary>
/// A MessagePack resolver (D5) that serializes Harmony DTO / broadcast-payload types as string-keyed
/// maps whose keys are <b>camelCase</b>, identical to what the SignalR JSON protocol emits (its
/// default <c>JsonSerializerDefaults.Web</c> options use <see cref="JsonNamingPolicy.CamelCase"/>).
/// MessagePack's own <c>ContractlessStandardResolver</c> writes the CLR member names verbatim
/// (PascalCase); the JavaScript client is hardcoded to read camelCase, so PascalCase keys would make
/// every field <c>undefined</c>. Composing this ahead of <c>ContractlessStandardResolver</c> makes the
/// MessagePack and JSON hub protocols share ONE wire shape, so the client is protocol-agnostic.
///
/// <para>Keys are computed with the very same <see cref="JsonNamingPolicy.CamelCase"/> (and honour
/// <see cref="JsonPropertyNameAttribute"/>) the JSON protocol uses, so the two protocols cannot drift.
/// Field <em>values</em> are delegated back to the composed resolver, so <c>long</c>→string (see
/// <see cref="MessagePackLongAsStringResolver"/>), nested DTOs, collections and enums all still resolve
/// correctly.</para>
///
/// <para><b>Serialize-only.</b> These types are broadcast-only (server→client) — every ChatHub method
/// argument is a primitive/string/collection, so the server never deserializes a Harmony DTO from
/// MessagePack. <see cref="CamelCaseObjectFormatter{T}.Deserialize"/> therefore throws; if it ever
/// fires, a hub method started taking a DTO argument and this resolver needs a real reader.</para>
/// </summary>
public sealed class MessagePackCamelCaseResolver : IFormatterResolver
{
    public static readonly MessagePackCamelCaseResolver Instance = new();

    private MessagePackCamelCaseResolver() { }

    public IMessagePackFormatter<T>? GetFormatter<T>() => Cache<T>.Formatter;

    private static bool AppliesTo(Type t) =>
        t.IsClass
        && !t.IsAbstract
        && t.Namespace?.StartsWith("Harmony", StringComparison.Ordinal) == true;

    private static class Cache<T>
    {
        public static readonly IMessagePackFormatter<T>? Formatter =
            AppliesTo(typeof(T)) ? new CamelCaseObjectFormatter<T>() : null;
    }
}

internal sealed class CamelCaseObjectFormatter<T> : IMessagePackFormatter<T>
{
    private readonly record struct Member(string Key, Type Type, Func<object, object?> Get);

    private static readonly Member[] Members = BuildMembers();

    private static Member[] BuildMembers()
    {
        var members = new List<Member>();
        foreach (
            var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        )
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0)
                continue;

            // Exactly the JSON protocol's key: an explicit [JsonPropertyName], else camelCase.
            var key =
                p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name);

            members.Add(new Member(key, p.PropertyType, obj => p.GetValue(obj)));
        }
        return members.ToArray();
    }

    public void Serialize(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        writer.WriteMapHeader(Members.Length);
        foreach (var member in Members)
        {
            writer.Write(member.Key);
            // Delegate the value to the composed resolver (long→string, nested DTOs, collections, enums).
            MessagePackSerializer.Serialize(member.Type, ref writer, member.Get(value), options);
        }
    }

    public T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
        throw new NotSupportedException(
            $"{typeof(T)} is a broadcast-only DTO; the server never deserializes it from MessagePack "
                + "(every hub-method argument is a primitive). If a hub method now takes this type, "
                + "implement a real reader here."
        );
}
