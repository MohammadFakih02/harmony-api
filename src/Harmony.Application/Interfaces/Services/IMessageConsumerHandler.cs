using Harmony.Domain.Interfaces;

namespace Harmony.Domain.Interfaces;

public interface IMessageConsumerHandler
{
    Task HandleMessageSentAsync(MessageSentEvent evt, CancellationToken ct = default);
    Task HandleMessageDeletedAsync(MessageDeletedEvent evt, CancellationToken ct = default);
    Task HandleMessageEditedAsync(MessageEditedEvent evt, CancellationToken ct = default);
}
