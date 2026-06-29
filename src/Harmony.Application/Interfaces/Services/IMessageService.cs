using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;

namespace Harmony.Domain.Interfaces.Services;

public interface IMessageService
{
    Task<SendMessageResponse> SendMessageAsync(
        long userId,
        long? guildId,
        long channelId,
        SendMessageRequest request,
        CancellationToken ct = default
    );

    Task<ChannelMessagesResponse> GetChannelMessagesAsync(
        long userId,
        long? guildId,
        long channelId,
        int limit = 50,
        long? beforeMessageId = null,
        CancellationToken ct = default
    );

    Task DeleteMessageAsync(
        long userId,
        long? guildId,
        long channelId,
        long messageId,
        CancellationToken ct = default
    );

    Task EditMessageAsync(
        long userId,
        long? guildId,
        long channelId,
        long messageId,
        EditMessageRequest request,
        CancellationToken ct = default
    );

    /// <summary>
    /// Publishes a server-generated system message (e.g. a member-join welcome notice) into a
    /// guild channel. Bypasses the permission/timeout/attachment/content gates of a normal send —
    /// the caller (a controller/service) is responsible for authorization. The message flows
    /// through the same RabbitMQ → Scylla → broadcast pipeline; <paramref name="authorUserId"/>
    /// is the subject of the notice (e.g. the joining user) so the client can render it.
    /// Mentions are not parsed for system messages. Returns the minted message id.
    /// </summary>
    Task<long> PublishSystemMessageAsync(
        long guildId,
        long channelId,
        long authorUserId,
        string messageType,
        string content,
        CancellationToken ct = default
    );
}
