using System.Collections.Concurrent;

namespace Heartbeat.Api.Live;

/// <summary>Un <see cref="HeartRateBuffer"/> en memoria por astronauta: las lecturas de un reloj nunca se mezclan con las de otro.</summary>
public sealed class HeartRateStore
{
    private readonly ConcurrentDictionary<Guid, HeartRateBuffer> _buffers = new();

    public HeartRateBuffer For(Guid astronautId) => _buffers.GetOrAdd(astronautId, _ => new HeartRateBuffer());

    public HeartRateBuffer? Find(Guid astronautId) => _buffers.GetValueOrDefault(astronautId);
}
