using Heartbeat.Api.Data;
using Heartbeat.Shared;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Crew;

/// <summary>
/// SANS view (HB-BE-05): IOP and visual acuity readings, pushed by the desktop in the sync, with their trend and their
/// correlation with the CO2 of the same period. The maths lives in <see cref="VisionAnalysis"/> (Heartbeat.Shared).
/// </summary>
public static class VisionSummaries
{
    // SQLite cannot compare a DateTimeOffset, so the period is applied in memory.
    public static async Task<VisionSummaryDto> LoadAsync(
        AppDbContext db, Guid astronautId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var ids = new[] { VisionAnalysis.IopId, VisionAnalysis.VisualAcuityId, VisionAnalysis.Co2Id };
        var rows = (await db.EnvironmentReadings.AsNoTracking()
                .Where(r => r.AstronautId == astronautId && ids.Contains(r.IndicatorId))
                .ToListAsync(cancellationToken))
            .Where(r => from is null || r.RecordedAt >= from)
            .Where(r => to is null || r.RecordedAt <= to)
            .ToList();

        IEnumerable<EnvironmentReadingStatus> Of(string id) => rows
            .Where(r => r.IndicatorId == id)
            .Select(EnvironmentReadings.ToStatus)
            .Where(s => s is not null)
            .Select(s => s!);

        var co2 = rows.Where(r => r.IndicatorId == VisionAnalysis.Co2Id).Select(r => new VisionPoint(r.RecordedAt, r.Value));
        return VisionAnalysis.Analyze(Of(VisionAnalysis.IopId), Of(VisionAnalysis.VisualAcuityId), co2, from, to);
    }

    /// <summary>The newest reading of each vision indicator, for the unified risk status.</summary>
    public static IReadOnlyList<EnvironmentReadingStatus> Latest(VisionSummaryDto summary)
        => new[] { summary.Iop, summary.VisualAcuity }
            .Where(t => t.Readings.Count > 0)
            .Select(t => t.Readings[^1])
            .ToList();
}
