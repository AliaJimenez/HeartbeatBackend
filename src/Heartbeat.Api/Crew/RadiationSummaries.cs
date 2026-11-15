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

        var doses = rows
            .OrderByDescending(d => d.RecordedAt)
            .Select(d => new RadiationDoseDto(d.Id, d.RecordedAt, d.ReceivedAt, d.DoseMsv, d.Note, d.Type))
            .ToList();

        return new RadiationSummaryDto(
            RadiationDoses.Total(rows.Select(d => d.DoseMsv)),
            doses.Count,
            doses,
            ReferenceRanges.GetSource(RadiationDoses.SourceId));
    }
}
