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

        var mission = await db.Astronauts
            .AsNoTracking()
            .Where(a => a.Id == astronautId)
            .Select(a => new { a.Mission.Environment, a.Mission.EnvironmentType })
            .FirstOrDefaultAsync(cancellationToken);
        var environment = EnvironmentOf(mission?.EnvironmentType, mission?.Environment);

        var doses = rows
            .OrderByDescending(d => d.RecordedAt)
            .Select(d => new RadiationDoseDto(d.Id, d.RecordedAt, d.ReceivedAt, d.DoseMsv, d.Note, d.Type))
            .ToList();

        return Build(doses, environment);
    }

    /// <summary>
    /// RadiationSummary v2 (CONTRACTS.md §10). <c>PercentOfLimit</c> is the cumulative dose against the 600 mSv career
    /// limit; <c>SpeDoseMsv</c> is the largest single solar-particle event, judged against the 250 mSv per-event limit;
    /// <c>Status</c> is the worse of the two. The dominant <c>Type</c> is the one that contributed the most dose.
    /// </summary>
    public static RadiationSummaryDto Build(IReadOnlyList<RadiationDoseDto> doses, string environment)
    {
        var total = RadiationDoses.Total(doses.Select(d => d.DoseMsv));
        var speDose = doses.Where(d => d.Type == RadiationType.Spe).Select(d => d.DoseMsv).DefaultIfEmpty(0m).Max();
        var type = doses.GroupBy(d => d.Type)
            .OrderByDescending(g => g.Sum(d => d.DoseMsv))
            .Select(g => g.Key)
            .FirstOrDefault(RadiationType.Gcr);

        var careerPercent = RadiationDoses.PercentOfCareerLimit(total);
        var spePercent = RadiationDoses.PercentOfSpeLimit(speDose);
        var status = RadiationDoses.WorstOf(
            RadiationDoses.StatusForCareerPercent(careerPercent),
            RadiationDoses.StatusForSpePercent(spePercent));

        return new RadiationSummaryDto(
            total,
            doses.Count,
            doses,
            ReferenceRanges.GetSource(RadiationDoses.SourceId),
            type,
            RadiationDoses.RateForEnvironment(environment),
            careerPercent,
            speDose,
            spePercent,
            status);
    }

    // The environment profile (HB-BE-01) supersedes the older Mission.Environment; both default to "station",
    // so whichever one was set to something else wins.
    public static string EnvironmentOf(string? profileType, string? legacyEnvironment)
    {
        const string Default = "station";
        if (!string.IsNullOrWhiteSpace(profileType) && !profileType.Equals(Default, StringComparison.OrdinalIgnoreCase))
        {
            return profileType;
        }

        return string.IsNullOrWhiteSpace(legacyEnvironment) ? Default : legacyEnvironment;
    }

    public static HealthStatus StatusFor(RadiationSummaryDto summary) => summary.Status;
}
