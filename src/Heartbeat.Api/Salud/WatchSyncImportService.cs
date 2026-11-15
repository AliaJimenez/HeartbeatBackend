using Heartbeat.Api.Data;
using Heartbeat.Api.Live;
using Heartbeat.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Heartbeat.Api.Salud;

/// <summary>Importa datos nuevos de Samsung Health y deja que los ids externos hagan cada vuelta idempotente.</summary>
public sealed class WatchSyncImportService(
    AppDbContext db,
    IWatchSyncClients clients,
    WearableReadingWriter writer,
    IOptions<WatchSyncOptions> options,
    ILogger<WatchSyncImportService> logger)
{
    public async Task ImportAllAsync(DateTimeOffset now, CancellationToken ct)
    {
        var configured = options.Value;
        var from = now.AddDays(-Math.Clamp(configured.ImportLookbackDays, 1, 30));

        // Una cuenta WatchSync por astronauta. Si tiene varios relojes, el más antiguo representa la fuente Samsung.
        var watches = (await db.WatchDevices.AsNoTracking()
                .Include(w => w.Astronaut)
                .ToListAsync(ct))
            .OrderBy(w => w.CreatedAt)
            .GroupBy(w => w.AstronautId)
            .Select(g => g.First())
            .ToList();

        foreach (var watch in watches)
        {
            try
            {
                var client = clients.For(watch.Astronaut.UserName);
                var sleep = await client.GetSleepAsync(from, now, ct);
                foreach (var record in sleep)
                {
                    await writer.SaveAsync(
                        watch.AstronautId,
                        watch.DeviceId,
                        $"watchsync:sleep:{watch.AstronautId:N}:{record.Id}",
                        record.EndAt,
                        IndicatorType.Sleep,
                        record.DurationMin / 60m,
                        null,
                        ct);
                }

                var pressure = await client.GetBloodPressureAsync(from, now, ct);
                foreach (var record in pressure)
                {
                    await writer.SaveAsync(
                        watch.AstronautId,
                        watch.DeviceId,
                        $"watchsync:blood-pressure:{watch.AstronautId:N}:{record.Id}",
                        record.MeasuredAt,
                        IndicatorType.BloodPressure,
                        record.Systolic,
                        record.Diastolic,
                        ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // No se mueve cursor: la siguiente vuelta vuelve a consultar la ventana y reintenta lo que faltó.
                logger.LogWarning(ex, "WatchSync import failed for astronaut {AstronautId}; it will retry", watch.AstronautId);
            }
        }
    }
}
