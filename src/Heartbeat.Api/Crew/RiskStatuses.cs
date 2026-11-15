using Heartbeat.Api.Data;
using Heartbeat.Shared;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Crew;

/// <summary>
/// Gathers what the risk engine needs for one astronaut (HB-BE-07) and runs <see cref="RiskEngine"/>. The engine itself
/// lives in Heartbeat.Shared so the desktop gets the same result offline; this only reads the database.
/// </summary>
public static class RiskStatuses
{
    public static async Task<RiskStatus> LoadAsync(
        AppDbContext db,
        Guid astronautId,
        decimal hoursWithoutContact,
        decimal continuousStayHours,
        CancellationToken cancellationToken)
    {
        var vitals = await LoadVitalsAsync(db, astronautId, cancellationToken);
        var radiation = await RadiationSummaries.LoadAsync(db, astronautId, cancellationToken);
        var environment = (await EnvironmentReadings.LoadAsync(db, astronautId, null, null, null, 1, 1, cancellationToken)).Latest;

        var vision = VisionSummaries.Latest(await VisionSummaries.LoadAsync(db, astronautId, null, null, cancellationToken));

        var mission = await db.Astronauts.AsNoTracking()
            .Where(a => a.Id == astronautId)
            .Select(a => a.Mission)
            .FirstOrDefaultAsync(cancellationToken);
        var missionDto = mission is null ? null : McMissionEndpoints.ToDto(mission, hoursWithoutContact, continuousStayHours);

        return RiskEngine.Evaluate(new RiskInputs(
            vitals,
            radiation,
            environment,
            missionDto?.EnvironmentProfile,
            missionDto?.Indices,
            Vision: vision));
    }

    // The newest value of each vital sign across the astronaut's check-ups (corrected check-ups are replaced by
    // their correction), evaluated again with the shared evaluator so labels and guidance come from the same ranges.
    private static async Task<CheckUpEvaluation?> LoadVitalsAsync(AppDbContext db, Guid astronautId, CancellationToken ct)
    {
        var checkUps = await db.CheckUps.AsNoTracking()
            .Where(c => c.AstronautId == astronautId)
            .Select(c => new { c.Id, c.RecordedAt, c.SupersedesId })
            .ToListAsync(ct);
        var superseded = checkUps.Where(c => c.SupersedesId != null).Select(c => c.SupersedesId!.Value).ToHashSet();
        var recordedAt = checkUps.Where(c => !superseded.Contains(c.Id)).ToDictionary(c => c.Id, c => c.RecordedAt);
        if (recordedAt.Count == 0)
        {
            return null;
        }

        var ids = recordedAt.Keys.ToList();
        var readings = await db.Readings.AsNoTracking().Where(r => ids.Contains(r.CheckUpId)).ToListAsync(ct);
        var inputs = readings
            .GroupBy(r => r.Indicator)
            .Select(g => g.OrderByDescending(r => recordedAt[r.CheckUpId]).First())
            .OrderBy(r => r.Indicator)
            .Select(r => new ReadingInput(r.Indicator, r.Value, r.SecondaryValue))
            .ToList();

        return inputs.Count == 0 ? null : new HealthEvaluator().Evaluate(inputs);
    }
}
