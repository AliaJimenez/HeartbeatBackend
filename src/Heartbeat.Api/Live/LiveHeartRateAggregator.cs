namespace Heartbeat.Api.Live;

public sealed record HeartRateMinute(
    Guid WatchId,
    Guid AstronautId,
    string DeviceId,
    DateTimeOffset Minute,
    decimal Median);

/// <summary>Agrupa las muestras por reloj y minuto; cada minuto se entrega una sola vez para persistir su mediana.</summary>
public sealed class LiveHeartRateAggregator
{
    private sealed class Bucket(Guid watchId, Guid astronautId, string deviceId, DateTimeOffset minute)
    {
        public Guid WatchId { get; } = watchId;
        public Guid AstronautId { get; } = astronautId;
        public string DeviceId { get; } = deviceId;
        public DateTimeOffset Minute { get; } = minute;
        public List<int> Values { get; } = [];
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Bucket> _buckets = [];

    public HeartRateMinute? Add(Guid watchId, Guid astronautId, string deviceId, HeartRateReading reading)
    {
        var minute = StartOfMinute(reading.MeasuredAt);
        lock (_gate)
        {
            if (!_buckets.TryGetValue(watchId, out var bucket))
            {
                bucket = new Bucket(watchId, astronautId, deviceId, minute);
                _buckets[watchId] = bucket;
            }
            else if (minute < bucket.Minute)
            {
                return null;
            }
            else if (minute > bucket.Minute)
            {
                var completed = Complete(bucket);
                bucket = new Bucket(watchId, astronautId, deviceId, minute);
                _buckets[watchId] = bucket;
                bucket.Values.Add(reading.Bpm);
                return completed;
            }

            bucket.Values.Add(reading.Bpm);
            return null;
        }
    }

    public IReadOnlyList<HeartRateMinute> DrainCompleted(DateTimeOffset now)
    {
        var currentMinute = StartOfMinute(now);
        lock (_gate)
        {
            var completed = _buckets.Values.Where(b => b.Minute < currentMinute).Select(Complete).ToList();
            foreach (var item in completed)
            {
                _buckets.Remove(item.WatchId);
            }

            return completed;
        }
    }

    private static DateTimeOffset StartOfMinute(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }

    private static HeartRateMinute Complete(Bucket bucket)
    {
        var sorted = bucket.Values.Order().ToArray();
        decimal median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2m;
        return new HeartRateMinute(bucket.WatchId, bucket.AstronautId, bucket.DeviceId, bucket.Minute, median);
    }
}
