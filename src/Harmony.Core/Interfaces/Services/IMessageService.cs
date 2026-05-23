using Harmony.Core.DTOs.Requests;
using Harmony.Core.DTOs.Responses;

namespace Harmony.Core.Interfaces.Services;

public interface IMessageService
{
    Task<SendMessageResponse> SendMessageAsync(
        long userId,
        long guildId,
        long channelId,
        SendMessageRequest request,
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
