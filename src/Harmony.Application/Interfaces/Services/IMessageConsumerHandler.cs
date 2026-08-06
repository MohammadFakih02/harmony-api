using System.Threading;
using System.Threading.Tasks;
using Harmony.Domain.Interfaces;

namespace Harmony.Domain.Interfaces;

public interface IMessageConsumerHandler
{
    Task HandleMessageSentAsync(MessageSentEvent evt, CancellationToken ct = default);
    Task HandleMessageDeletedAsync(MessageDeletedEvent evt, CancellationToken ct = default);
    Task HandleMessageEditedAsync(MessageEditedEvent evt, CancellationToken ct = default);

    /// <summary>Consumer contract to handle ScyllaDB and relational database purges on channel deletion [12].</summary>
    Task HandleChannelDeletedAsync(ChannelDeletedEvent evt, CancellationToken ct = default);
}
