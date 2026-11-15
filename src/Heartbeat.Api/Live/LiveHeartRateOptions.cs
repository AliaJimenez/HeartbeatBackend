namespace Heartbeat.Api.Live;

public sealed class LiveHeartRateOptions
{
    /// <summary>Tiempo continuo en estado crítico antes de crear una alerta.</summary>
    public int CriticalDurationSeconds { get; set; } = 60;
}
