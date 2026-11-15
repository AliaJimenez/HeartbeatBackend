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
}
