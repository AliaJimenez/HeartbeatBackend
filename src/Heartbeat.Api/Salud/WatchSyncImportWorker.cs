using Microsoft.Extensions.Options;

namespace Heartbeat.Api.Salud;

/// <summary>Ejecuta el importador periódicamente; cualquier caída de WatchSync se reintenta en la vuelta siguiente.</summary>
public sealed class WatchSyncImportWorker(
    IServiceScopeFactory scopes,
    IOptions<WatchSyncOptions> options,
    ILogger<WatchSyncImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Clamp(options.Value.ImportIntervalMinutes, 1, 1440));
        using var timer = new PeriodicTimer(interval);

        // Esperar la primera vuelta evita competir con migraciones/seed durante el arranque.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var importer = scope.ServiceProvider.GetRequiredService<WatchSyncImportService>();
                await importer.ImportAllAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "WatchSync import cycle failed; the next cycle will retry");
            }
        }
    }
}
