using Heartbeat.Shared;
using Microsoft.Extensions.Options;

namespace Heartbeat.Api.Live;

public sealed record HeartRateStatusEvent(
    int Bpm,
    DateTimeOffset MeasuredAt,
    HealthStatus Status,
    IReadOnlyList<string> BandIds);

public sealed record LiveHeartRateObservation(
    HeartRateStatusEvent Event,
    bool ShouldCreateAlert,
    DateTimeOffset? CriticalSince);

/// <summary>Evalúa cada muestra y conserva el estado mínimo necesario para emitir una alerta por episodio crítico.</summary>
public sealed class LiveHeartRateMonitor
{
    private sealed class WatchState
    {
        public DateTimeOffset LastMeasuredAt { get; set; }
        public DateTimeOffset? CriticalSince { get; set; }
        public bool AlertReserved { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, WatchState> _states = [];
    private readonly IHealthEvaluator _evaluator;
    private readonly TimeSpan _criticalDuration;

    public LiveHeartRateMonitor(IHealthEvaluator evaluator, IOptions<LiveHeartRateOptions> options)
    {
        _evaluator = evaluator;
        _criticalDuration = TimeSpan.FromSeconds(Math.Max(1, options.Value.CriticalDurationSeconds));
    }

    public LiveHeartRateObservation Observe(Guid watchId, HeartRateReading reading)
    {
        var evaluation = _evaluator.Evaluate(new ReadingInput(IndicatorType.HeartRate, reading.Bpm));
        var statusEvent = new HeartRateStatusEvent(reading.Bpm, reading.MeasuredAt, evaluation.Status, evaluation.BandIds);

        lock (_gate)
        {
            if (!_states.TryGetValue(watchId, out var state))
            {
                state = new WatchState { LastMeasuredAt = reading.MeasuredAt };
                _states[watchId] = state;
            }
            else if (reading.MeasuredAt <= state.LastMeasuredAt)
            {
                // La muestra se evalúa y se publica, pero una muestra atrasada no altera el episodio actual.
                return new LiveHeartRateObservation(statusEvent, false, state.CriticalSince);
            }
            else
            {
                state.LastMeasuredAt = reading.MeasuredAt;
            }

            if (evaluation.Status != HealthStatus.Critical)
            {
                state.CriticalSince = null;
                state.AlertReserved = false;
                return new LiveHeartRateObservation(statusEvent, false, null);
            }

            state.CriticalSince ??= reading.MeasuredAt;
            var shouldCreate = !state.AlertReserved
                && reading.MeasuredAt - state.CriticalSince.Value >= _criticalDuration;
            if (shouldCreate)
            {
                state.AlertReserved = true;
            }

            return new LiveHeartRateObservation(statusEvent, shouldCreate, state.CriticalSince);
        }
    }

    public void ReleaseAlertReservation(Guid watchId, DateTimeOffset criticalSince)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(watchId, out var state) && state.CriticalSince == criticalSince)
            {
                state.AlertReserved = false;
            }
        }
    }
}
