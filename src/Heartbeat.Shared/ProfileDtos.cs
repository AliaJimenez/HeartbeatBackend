namespace Heartbeat.Shared;

/// <summary>A threshold band of a profile indicator: <c>Min &lt;= value &lt; Max</c> (an open end is unbounded).</summary>
public sealed record ProfileBandDto(HealthStatus Status, decimal? Min, decimal? Max);

/// <summary>
/// One indicator as a domain profile sees it: the profile's own thresholds when it overrides the catalog
/// (<see cref="Overridden"/>), the catalog's otherwise, with their status and source.
/// </summary>
public sealed record ProfileIndicatorDto(
    string IndicatorId,
    string Name,
    string Unit,
    CatalogStatus CatalogStatus,
    SourceLink Source,
    bool Overridden,
    IReadOnlyList<ProfileBandDto> Bands);

/// <summary>The indicators of one of the five hazards under a profile (empty when the catalog has none for it).</summary>
public sealed record ProfileHazardDto(Hazard Hazard, IReadOnlyList<ProfileIndicatorDto> Indicators);

/// <summary>
/// GET /api/mc/v1/profiles item (HB-BE-11): a domain profile (space reference, mining, Antarctica, submarine,
/// offshore) with a typical environment and the thresholds of the five hazards. Always simulated data.
/// </summary>
public sealed record DomainProfileDto(
    string Id,
    string Name,
    string Description,
    EnvironmentProfile Environment,
    CatalogStatus Status,
    SourceLink Source,
    IReadOnlyList<ProfileHazardDto> Hazards,
    bool Simulated = true);
