using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.EntityFrameworkCore;
using EnvironmentReadingEntity = Heartbeat.Api.Data.Entities.EnvironmentReading;
using EnvironmentReadingDto = Heartbeat.Shared.EnvironmentReading;

namespace Heartbeat.Api.Crew;

/// <summary>
/// Habitat readings (CO2, O2, pressure, temperature, humidity, light, noise) pushed by the desktop in
/// <c>POST /sync</c> and read by Mission Control (HB-BE-03). Bands and sources come from the indicator catalog
/// (<c>contracts/indicators.catalog.json</c>); nothing about thresholds lives here. All data is simulated.
/// </summary>
public static class EnvironmentReadings
{
    public const int NoteMaxLength = 200;
    public const decimal MaxAbsValue = 100_000m;

    /// <summary>The catalog indicators that describe the habitat: those of the hostile closed environment hazard.</summary>
    public static IndicatorCatalogEntry? EnvironmentIndicator(string? id)
        => IndicatorsCatalog.Find(id) is { Hazard: Hazard.HostileClosedEnvironment } entry ? entry : null;

    public static IReadOnlyList<IndicatorCatalogEntry> EnvironmentIndicators()
        => IndicatorsCatalog.Cached.Where(e => e.Hazard == Hazard.HostileClosedEnvironment).ToList();

    // Syncs like check-ups and doses: the reading is created on the desktop with its own id, so a retry is harmless.
    // A critical band creates one Alert (once per reading; and not again while an alert for that indicator is open).
    public static async Task SyncAsync(
        AppDbContext db,
        Guid astronautId,
        Guid missionId,
        IReadOnlyList<EnvironmentReadingDto> readings,
        DateTimeOffset serverNow,
        List<Guid> accepted,
        List<SyncRejected> rejected,
        CancellationToken cancellationToken)
    {
        if (readings.Count == 0)
        {
            return;
        }

        var incomingIds = readings.Select(r => r.Id).Distinct().ToList();
        var known = await db.EnvironmentReadings.AsNoTracking()
            .Where(r => incomingIds.Contains(r.Id))
            .Select(r => new { r.Id, r.AstronautId })
            .ToListAsync(cancellationToken);
        var mine = known.Where(r => r.AstronautId == astronautId).Select(r => r.Id).ToHashSet();
        var ownedByOthers = known.Where(r => r.AstronautId != astronautId).Select(r => r.Id).ToHashSet();
        var seenThisSync = new HashSet<Guid>();

        foreach (var dto in readings)
        {
            if (ownedByOthers.Contains(dto.Id))
            {
                rejected.Add(new SyncRejected(dto.Id, "Environment reading id is already in use"));
                continue;
            }

            if (mine.Contains(dto.Id) || !seenThisSync.Add(dto.Id))
            {
                accepted.Add(dto.Id);
                continue;
            }

            var entry = EnvironmentIndicator(dto.IndicatorId);
            var error = Validate(dto, entry);
            if (error is not null)
            {
                rejected.Add(new SyncRejected(dto.Id, error));
                continue;
            }

            var band = IndicatorsCatalog.Evaluate(entry!, dto.Value);
            db.EnvironmentReadings.Add(new EnvironmentReadingEntity
            {
                Id = dto.Id,
                AstronautId = astronautId,
                MissionId = missionId,
                IndicatorId = entry!.Id,
                Value = dto.Value,
                SecondaryValue = dto.SecondaryValue,
                Unit = entry.Unit,
                RecordedAt = dto.RecordedAt,
                ReceivedAt = serverNow,
                Simulated = true,
                Note = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim(),
            });
            accepted.Add(dto.Id);

            if (band.Status == HealthStatus.Critical)
            {
                await RaiseAlertAsync(db, astronautId, entry, band, dto.Value, serverNow, cancellationToken);
            }
        }
    }

    public static string? Validate(EnvironmentReadingDto dto, IndicatorCatalogEntry? entry)
    {
        if (entry is null)
        {
            return $"'{dto.IndicatorId}' is not an environment indicator of the catalog";
        }

        if (Math.Abs(dto.Value) > MaxAbsValue || (dto.SecondaryValue is { } s && Math.Abs(s) > MaxAbsValue))
        {
            return $"Environment value is out of range for {entry.Id}";
        }

        if (dto.Note is { Length: > NoteMaxLength })
        {
            return $"Environment note cannot exceed {NoteMaxLength} characters";
        }

        return null;
    }

    private static async Task RaiseAlertAsync(
        AppDbContext db,
        Guid astronautId,
        IndicatorCatalogEntry entry,
        IndicatorBand band,
        decimal value,
        DateTimeOffset serverNow,
        CancellationToken cancellationToken)
    {
        var marker = AlertMarker(entry.Id);
        var openAlready = await db.Alerts.AnyAsync(
            a => a.AstronautId == astronautId && a.Indicators == marker && a.AcknowledgedAt == null, cancellationToken)
            || db.Alerts.Local.Any(a => a.AstronautId == astronautId && a.Indicators == marker && a.AcknowledgedAt == null);
        if (openAlready)
        {
            return;
        }

        db.Alerts.Add(new Alert
        {
            Id = Guid.CreateVersion7(),
            AstronautId = astronautId,
            Kind = AlertKind.Environment,
            Indicators = marker,
            Message = AlertMessage(entry, band, value),
            CreatedAt = serverNow,
        });
    }

    /// <summary>Stored in <c>Alert.Indicators</c> for environment alerts (they have no <c>IndicatorType</c>).</summary>
    public static string AlertMarker(string indicatorId) => $"environment:{indicatorId}";

    public static string AlertMessage(IndicatorCatalogEntry entry, IndicatorBand band, decimal value)
    {
        var range = (band.Min, band.Max) switch
        {
            ({ } min, { } max) => $"{min:0.##} to {max:0.##} {entry.Unit}",
            ({ } min, null) => $"at or above {min:0.##} {entry.Unit}",
            (null, { } max) => $"below {max:0.##} {entry.Unit}",
            _ => entry.Unit,
        };
        return $"{entry.Name} is critical (simulated): {value:0.##} {entry.Unit}, in the band {range}.";
    }

    // SQLite cannot compare or ORDER BY DateTimeOffset, so filtering, ordering and paging happen in memory.
    public static async Task<EnvironmentSummaryDto> LoadAsync(
        AppDbContext db,
        Guid astronautId,
        string? indicatorId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var rows = await db.EnvironmentReadings.AsNoTracking()
            .Where(r => r.AstronautId == astronautId)
            .ToListAsync(cancellationToken);

        var all = rows
            .Select(ToStatus)
            .Where(r => r is not null)
            .Select(r => r!)
            .OrderByDescending(r => r.RecordedAt)
            .ToList();

        var latest = all
            .GroupBy(r => r.IndicatorId)
            .Select(g => g.First())
            .OrderBy(r => r.IndicatorId, StringComparer.Ordinal)
            .ToList();
        var status = latest.Select(r => r.Status).DefaultIfEmpty(HealthStatus.Normal).Max();

        var filtered = all
            .Where(r => indicatorId is null || string.Equals(r.IndicatorId, indicatorId, StringComparison.OrdinalIgnoreCase))
            .Where(r => from is null || r.RecordedAt >= from)
            .Where(r => to is null || r.RecordedAt <= to)
            .ToList();

        var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new EnvironmentSummaryDto(status, latest, new Paged<EnvironmentReadingStatus>(items, page, pageSize, filtered.Count));
    }

    private static EnvironmentReadingStatus? ToStatus(EnvironmentReadingEntity r)
    {
        var entry = IndicatorsCatalog.Find(r.IndicatorId);
        if (entry is null)
        {
            return null;   // an indicator removed from the catalog since the reading was stored
        }

        var band = IndicatorsCatalog.Evaluate(entry, r.Value);
        return new EnvironmentReadingStatus(
            r.Id, r.RecordedAt, r.ReceivedAt, entry.Id, entry.Name, r.Value, r.SecondaryValue, entry.Unit,
            band.Status, string.IsNullOrWhiteSpace(band.Label) ? band.Status.ToString().ToLowerInvariant() : band.Label,
            entry.Status, IndicatorsCatalog.SourceFor(entry), r.Note, r.Simulated);
    }
}
