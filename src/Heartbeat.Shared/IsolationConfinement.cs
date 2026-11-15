using System.Text.Json;

namespace Heartbeat.Shared;

public static class IsolationConfinement
{
    public const string IsolationIndicatorId = "isolation";
    public const string ConfinementIndicatorId = "confinement";

    public const string SourceId = "nasa-delay";

    public const decimal WeightDelay = 0.3m;
    public const decimal WeightDistance = 0.2m;
    public const decimal WeightCrew = 0.3m;
    public const decimal WeightContact = 0.2m;

    public const decimal MinHabitableVolumePerPerson = 5.0m;

    public static decimal Normalize(decimal value, decimal min, decimal max)
    {
        if (max <= min)
        {
            return 0m;
        }

        var normalized = (value - min) / (max - min);
        if (normalized < 0m) normalized = 0m;
        if (normalized > 1m) normalized = 1m;
        return Math.Round(normalized, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Isolation index in [0,1]: higher means more isolated.
    /// 0.3*norm(delay) + 0.2*norm(distance) + 0.3*(1/sqrt(crew)) + 0.2*norm(hoursWithoutContact).
    /// </summary>
    public static decimal IsolationIndex(
        decimal commDelayMinutes,
        decimal distanceKm,
        int crewSize,
        decimal hoursWithoutContact,
        decimal delayMin = 0m,
        decimal delayMax = 30m,
        decimal distanceMin = 0m,
        decimal distanceMax = 225000000m,
        decimal contactMin = 0m,
        decimal contactMax = 24m)
    {
        if (crewSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(crewSize), "Crew size must be greater than zero.");
        }

        var delay = Normalize(commDelayMinutes, delayMin, delayMax);
        var distance = Normalize(distanceKm, distanceMin, distanceMax);
        var crewTerm = 1m / (decimal)Math.Sqrt(crewSize);
        if (crewTerm > 1m) crewTerm = 1m;
        var contact = Normalize(hoursWithoutContact, contactMin, contactMax);

        var index = WeightDelay * delay
                  + WeightDistance * distance
                  + WeightCrew * crewTerm
                  + WeightContact * contact;

        if (index < 0m) index = 0m;
        if (index > 1m) index = 1m;
        return Math.Round(index, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Confinement index in [0,1]: higher means more confined.
    /// Decreases with habitable volume per person and increases with continuous stay hours.
    /// </summary>
    public static decimal ConfinementIndex(
        decimal habitableVolumeM3,
        int crewSize,
        decimal continuousStayHours,
        decimal minVolumePerPerson = MinHabitableVolumePerPerson,
        decimal maxStayHours = 24m)
    {
        if (crewSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(crewSize), "Crew size must be greater than zero.");
        }

        var volumePerPerson = habitableVolumeM3 / crewSize;
        var volumeTerm = volumePerPerson <= 0 ? 1m : Math.Min(1m, minVolumePerPerson / volumePerPerson);
        var stayTerm = maxStayHours <= 0 ? 0m : Math.Min(1m, continuousStayHours / maxStayHours);

        var index = (volumeTerm + stayTerm) / 2m;
        if (index < 0m) index = 0m;
        if (index > 1m) index = 1m;
        return Math.Round(index, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>One-way light travel time to the mission, in minutes (distance / c).</summary>
    public static decimal CommDelayMinutes(decimal distanceKm) => distanceKm / SpeedOfLightKmPerSecond / 60m;

    public const decimal SpeedOfLightKmPerSecond = 299792.458m;

    /// <summary>
    /// Both indices for a mission environment, each placed in the status band of its catalog entry
    /// (<c>isolation</c> and <c>confinement</c> in contracts/indicators.catalog.json). The two time inputs are not part
    /// of the profile: pass what the app knows (0 gives the index of the profile alone).
    /// </summary>
    public static IsolationConfinementIndices Evaluate(
        EnvironmentProfile profile,
        decimal hoursWithoutContact = 0m,
        decimal continuousStayHours = 0m)
    {
        var isolation = IsolationIndex(
            CommDelayMinutes(profile.DistanceKm), profile.DistanceKm, profile.CrewSize, hoursWithoutContact);
        var confinement = ConfinementIndex(profile.HabitableVolumeM3, profile.CrewSize, continuousStayHours);

        var isolationDto = ToDto(IsolationIndicatorId, isolation);
        var confinementDto = ToDto(ConfinementIndicatorId, confinement);

        return new IsolationConfinementIndices(
            isolationDto,
            confinementDto,
            (HealthStatus)Math.Max((int)isolationDto.Status, (int)confinementDto.Status),
            hoursWithoutContact,
            continuousStayHours);
    }

    private static IsolationConfinementIndexDto ToDto(string indicatorId, decimal value)
    {
        var entry = IndicatorsCatalog.Find(indicatorId)
            ?? throw new InvalidOperationException($"The indicators catalog has no '{indicatorId}' entry.");
        var band = IndicatorsCatalog.Evaluate(entry, value);
        return new IsolationConfinementIndexDto(
            entry.Id,
            entry.Name,
            value,
            band.Status,
            string.IsNullOrWhiteSpace(band.Label) ? LevelWord(band.Status) : band.Label,
            entry.Status,
            IndicatorsCatalog.SourceFor(entry));
    }

    private static string LevelWord(HealthStatus status) => status switch
    {
        HealthStatus.Normal => "Low",
        HealthStatus.Watch => "Moderate",
        _ => "High",
    };

    /// <summary>
    /// The <see cref="Hazard.IsolationAndConfinement"/> traffic light built from the two indices: the worse of
    /// their statuses, with the worse index as value and the start of its critical band as limit.
    /// </summary>
    public static HazardStatus ToHazardStatus(IsolationConfinementIndices indices)
    {
        var worse = indices.Confinement.Status > indices.Isolation.Status ? indices.Confinement : indices.Isolation;
        var critical = IndicatorsCatalog.Find(worse.IndicatorId)?.Bands
            .FirstOrDefault(b => b.Status == HealthStatus.Critical)?.Min;

        var explanation = indices.Status == HealthStatus.Normal
            ? $"Isolation ({indices.Isolation.Value:0.##}) and confinement ({indices.Confinement.Value:0.##}) are low. Simulated indices, not a diagnosis."
            : $"Isolation is {indices.Isolation.Label.ToLowerInvariant()} ({indices.Isolation.Value:0.##}) and confinement is {indices.Confinement.Label.ToLowerInvariant()} ({indices.Confinement.Value:0.##}). Simulated indices, not a diagnosis.";

        return new HazardStatus(
            Hazard.IsolationAndConfinement,
            indices.Status,
            explanation,
            worse.Value,
            critical,
            "0-1",
            worse.Source);
    }

    /// <summary>
    /// Puts the isolation and confinement indices into a <see cref="RiskStatus"/>: replaces (or adds) the
    /// <see cref="Hazard.IsolationAndConfinement"/> hazard, keeps the global status as the worst hazard and attaches
    /// the indices. The global explanation is replaced only when this hazard raises the global status.
    /// </summary>
    public static RiskStatus WithIsolationConfinement(this RiskStatus risk, IsolationConfinementIndices indices)
    {
        var hazard = ToHazardStatus(indices);
        var hazards = risk.Hazards.Where(h => h.Hazard != Hazard.IsolationAndConfinement).Append(hazard)
            .OrderBy(h => h.Hazard)
            .ToList();
        var status = hazards.Max(h => h.Status);

        return risk with
        {
            Status = status,
            Explanation = status > risk.Status ? hazard.Explanation : risk.Explanation,
            Hazards = hazards,
            Indices = indices,
        };
    }
}
