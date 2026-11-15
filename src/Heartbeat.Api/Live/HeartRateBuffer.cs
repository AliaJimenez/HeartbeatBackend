namespace Heartbeat.Api.Live;

/// <summary>Últimas 600 lecturas del reloj (unos 10 minutos) en memoria, en orden de llegada. Seguro entre hilos.</summary>
public sealed class HeartRateBuffer
{
    public const int Capacity = 600;

    private readonly object _gate = new();
    private readonly Queue<HeartRateReading> _readings = new(Capacity);
    private HeartRateReading? _latest;

    public HeartRateReading? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    public void Add(HeartRateReading r)
    {
        ArgumentNullException.ThrowIfNull(r);
        lock (_gate)
        {
            _readings.Enqueue(r);
            while (_readings.Count > Capacity)
            {
                _readings.Dequeue();
            }

            _latest = r;
        }
    }

    /// <summary>Copia de las lecturas medidas desde <paramref name="from"/>, en orden cronológico.</summary>
    public IReadOnlyList<HeartRateReading> Since(DateTimeOffset from)
    {
        lock (_gate)
        {
            return _readings.Where(r => r.MeasuredAt >= from).OrderBy(r => r.MeasuredAt).ToList();
        }
    }
}
