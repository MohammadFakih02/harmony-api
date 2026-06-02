using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;

namespace Harmony.Domain.Interfaces.Services;

public interface IMessageService
{
    Task<SendMessageResponse> SendMessageAsync(
        long userId,
        long guildId,
        long channelId,
        SendMessageRequest request,
        CancellationToken ct = default
    );

    Task<IEnumerable<MessageResponse>> GetChannelMessagesAsync(
        long userId,
        long guildId,
        long channelId,
        int limit = 50,
        long? beforeMessageId = null,
        CancellationToken ct = default
    );

    Task DeleteMessageAsync(
        long userId,
        long guildId,
        long channelId,
        long messageId,
        CancellationToken ct = default
    );

    Task EditMessageAsync(
        long userId,
        long guildId,
        long channelId,
        long messageId,
        EditMessageRequest request,
        CancellationToken ct = default
    );
}
