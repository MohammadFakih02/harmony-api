using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces;
using Orleans;

namespace Harmony.API.Grains;

/// <summary>
/// The D2a <see cref="IChannelDispatcher"/> used outside the Test environment: route the send to the
/// per-channel <see cref="IChannelGrain"/> (keyed by channelId), which broadcasts first and then
/// republishes the event as the durable persist log. Thin, like <c>GrainPresenceService</c> — all
/// the logic lives in the grain. The Test environment keeps <c>LegacyChannelDispatcher</c> (Orleans
/// is not co-hosted there).
/// </summary>
public sealed class GrainChannelDispatcher : IChannelDispatcher
{
    private readonly IGrainFactory _grains;

    public GrainChannelDispatcher(IGrainFactory grains) => _grains = grains;

    public Task DispatchSentAsync(MessageSentEvent evt, CancellationToken ct = default) =>
        _grains.GetGrain<IChannelGrain>(evt.ChannelId).SendMessage(evt);

    public Task DispatchEditedAsync(MessageEditedEvent evt, CancellationToken ct = default) =>
        _grains.GetGrain<IChannelGrain>(evt.ChannelId).EditMessage(evt);

    public Task DispatchDeletedAsync(MessageDeletedEvent evt, CancellationToken ct = default) =>
        _grains.GetGrain<IChannelGrain>(evt.ChannelId).DeleteMessage(evt);
}
