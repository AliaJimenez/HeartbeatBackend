using Heartbeat.Shared;

namespace Heartbeat.Api.Live;

/// <summary>Vacía minutos completos aunque el reloj deje de enviar justo al terminar uno.</summary>
public sealed class LiveHeartRatePersistenceService(
    LiveHeartRateAggregator aggregator,
    IServiceScopeFactory scopes,
    ILogger<LiveHeartRatePersistenceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var minute in aggregator.DrainCompleted(DateTimeOffset.UtcNow))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var writer = scope.ServiceProvider.GetRequiredService<WearableReadingWriter>();
                    await writer.SaveAsync(
                        minute.AstronautId,
                        minute.DeviceId,
                        $"live-heart-rate:{minute.WatchId:N}:{minute.Minute:O}",
                        minute.Minute,
                        IndicatorType.HeartRate,
                        minute.Median,
                        null,
                        stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Could not persist heart-rate minute for watch {WatchId}", minute.WatchId);
                }
            }
        }
    }
}
