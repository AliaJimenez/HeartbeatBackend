namespace Heartbeat.Shared;

/// <summary>
/// Traffic-light status for a single hazard (one of the five hazards in <see cref="Hazard"/>).
/// </summary>
public sealed record HazardStatus(
    Hazard Hazard,
    HealthStatus Status,
    string Explanation,
    decimal? Value = null,
    decimal? Limit = null,
    string? Unit = null,
    SourceLink? Source = null)
{
    public HealthStatus Level => Status;
    public string Reason => Explanation;
    public string Message => Explanation;
}

/// <summary>
/// Unified traffic-light status: a global status plus one <see cref="HazardStatus"/> per hazard,
/// with a plain-language explanation. Computed in Heartbeat.Shared so offline results match the server.
/// </summary>
public sealed record RiskStatus(
    HealthStatus Status,
    string Explanation,
    IReadOnlyList<HazardStatus> Hazards,
    IReadOnlyList<PersonalDeviation>? PersonalDeviations = null,
    IsolationConfinementIndices? Indices = null)
{
    public HealthStatus GlobalStatus => Status;
    public HealthStatus OverallStatus => Status;
    public string Reason => Explanation;
    public string Message => Explanation;
    public IReadOnlyList<HazardStatus> HazardStatuses => Hazards;
    public IReadOnlyList<PersonalDeviation> Deviations => PersonalDeviations ?? [];
}
/// <summary>
/// One isolation or confinement index (0 to 1, higher is worse) evaluated against its entry in
/// contracts/indicators.catalog.json: the band it falls in, the catalog status (verified | heuristic) and its source.
/// </summary>
public sealed record IsolationConfinementIndexDto(
    string IndicatorId,
    string Name,
    decimal Value,
    HealthStatus Status,
    string Label,
    CatalogStatus CatalogStatus,
    SourceLink Source);

/// <summary>
/// Both indices for a mission environment (HB-BE-06), the worse of their statuses, and the time inputs used.
/// Always simulated data; not a diagnosis.
/// </summary>
public sealed record IsolationConfinementIndices(
    IsolationConfinementIndexDto Isolation,
    IsolationConfinementIndexDto Confinement,
    HealthStatus Status,
    decimal HoursWithoutContact,
    decimal ContinuousStayHours,
    bool Simulated = true);
