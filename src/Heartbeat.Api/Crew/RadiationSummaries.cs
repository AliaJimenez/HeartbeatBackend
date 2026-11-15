using Heartbeat.Api.Data;
using Heartbeat.Shared;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Crew;

public static class RadiationSummaries
{
    // SQLite cannot ORDER BY a DateTimeOffset or SUM a decimal, so both happen in memory.
    public static async Task<RadiationSummaryDto> LoadAsync(AppDbContext db, Guid astronautId, CancellationToken cancellationToken)
    {
        var rows = await db.RadiationDoses
            .AsNoTracking()
            .Where(d => d.AstronautId == astronautId)
            .ToListAsync(cancellationToken);

        var speDose = rows.Where(d => d.Type == RadiationType.Spe).Select(d => d.DoseMsv).Sum();
        var total = RadiationDoses.Total(rows.Select(d => d.DoseMsv));
        var environment = rows.Select(d => d.Astronaut?.Mission?.Environment).FirstOrDefault() ?? "station";
        var type = rows.GroupBy(d => d.Type).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var rate = RadiationDoses.RateForEnvironment(environment);

        var doses = rows
            .OrderByDescending(d => d.RecordedAt)
            .Select(d => new RadiationDoseDto(d.Id, d.RecordedAt, d.ReceivedAt, d.DoseMsv, d.Note, d.Type))
            .ToList();

        return new RadiationSummaryDto(
            total,
            doses.Count,
            doses,
            ReferenceRanges.GetSource(RadiationDoses.SourceId),
            type == default ? RadiationType.Gcr : type,
            rate,
            RadiationDoses.PercentOfCareerLimit(total));
    }

    public static HealthStatus StatusFor(RadiationSummaryDto summary) =>
        RadiationDoses.StatusForCareerPercent(summary.PercentOfLimit);

    public static decimal SpePercent(decimal speDoseMsv) => RadiationDoses.PercentOfSpeLimit(speDoseMsv);
}
