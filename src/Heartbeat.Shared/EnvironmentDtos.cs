namespace Heartbeat.Shared;

/// <summary>
/// Mission environment profile (gravity, distance from Earth, crew size, habitable volume).
/// Used by the crew and Mission Control apps to describe the environment a crew lives in.
/// All values are simulated/demo data (see contracts/CONTRACTS.md §10).
/// </summary>
public sealed record EnvironmentProfile(
    string Type,
    decimal GravityG,
    decimal DistanceKm,
    int CrewSize,
    decimal HabitableVolumeM3)
{
    public decimal Gravity => GravityG;
    public decimal Distance => DistanceKm;
    public int Crew => CrewSize;
    public decimal HabitableVolume => HabitableVolumeM3;
}

/// <summary>
/// One environment reading pushed by the crew app during sync (CO2, O2, pressure, temperature, ...).
/// The indicator id refers to an entry in contracts/indicators.catalog.json.
/// </summary>
public sealed record EnvironmentReading(
    Guid Id,
    DateTimeOffset RecordedAt,
    DateTimeOffset? ReceivedAt,
    string IndicatorId,
    decimal Value,
    decimal? SecondaryValue = null,
    string? Unit = null,
    string? Note = null)
{
    public string Indicator => IndicatorId;
}