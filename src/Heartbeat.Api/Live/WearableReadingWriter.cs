using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Live;

/// <summary>Persiste una medición del reloj como un check-up de una lectura, la evalúa y opcionalmente levanta su alerta.</summary>
public sealed class WearableReadingWriter(AppDbContext db, IHealthEvaluator evaluator)
{
    public async Task<bool> SaveAsync(
        Guid astronautId,
        string deviceId,
        string externalId,
        DateTimeOffset recordedAt,
        IndicatorType indicator,
        decimal value,
        decimal? secondaryValue,
        CancellationToken ct,
        bool createCriticalAlert = true)
    {
        if (await db.Readings.AnyAsync(r => r.ExternalId == externalId, ct))
        {
            return false;
        }

        var input = new ReadingInput(indicator, value, secondaryValue);
        var errors = evaluator.Validate(input);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid wearable reading '{externalId}': {errors[0]}");
        }

        var evaluation = evaluator.Evaluate(input);
        var now = DateTimeOffset.UtcNow;
        var checkUp = new CheckUp
        {
            Id = Guid.CreateVersion7(),
            AstronautId = astronautId,
            RecordedAt = recordedAt,
            ReceivedAt = now,
            OverallStatus = evaluation.Status,
            RangesVersion = evaluator.RangesVersion,
            Note = "Imported from wearable",
        };
        checkUp.Readings.Add(new Reading
        {
            Id = Guid.CreateVersion7(),
            Indicator = indicator,
            Value = value,
            SecondaryValue = secondaryValue,
            Status = evaluation.Status,
            BandIds = string.Join(",", evaluation.BandIds),
            Source = ReadingSource.Wearable,
            DeviceId = deviceId,
            ExternalId = externalId,
        });
        db.CheckUps.Add(checkUp);

        if (createCriticalAlert && evaluation.Status == HealthStatus.Critical)
        {
            db.Alerts.Add(new Alert
            {
                Id = Guid.CreateVersion7(),
                AstronautId = astronautId,
                CheckUpId = checkUp.Id,
                Indicators = indicator.ToString(),
                CreatedAt = now,
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.Readings.AsNoTracking().AnyAsync(r => r.ExternalId == externalId, ct))
            {
                return false;
            }

            throw;
        }
    }
}
