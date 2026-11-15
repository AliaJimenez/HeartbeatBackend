namespace Heartbeat.Shared;

public enum TrendDirection
{
    Unknown,
    Stable,
    Rising,
    Falling
}

public enum CorrelationStrength
{
    Unknown,
    Weak,
    Moderate,
    Strong
}

/// <summary>One reading of a vision indicator or of CO2: when and how much.</summary>
public sealed record VisionPoint(DateTimeOffset RecordedAt, decimal Value);

/// <summary>
/// The trend of one indicator over the period: direction, the change the fitted line shows and its slope per day.
/// "Stable" is decided by the <c>vision.trend.*.stableDelta</c> rule of <c>indicators.catalog.json</c>.
/// </summary>
public sealed record IndicatorTrend(
    string IndicatorId,
    string Name,
    string Unit,
    int Count,
    TrendDirection Direction,
    decimal? Change,
    decimal? SlopePerDay,
    decimal? Latest,
    DateTimeOffset? LatestAt,
    HealthStatus Status,
    string Explanation,
    CatalogStatus CatalogStatus,
    SourceLink Source,
    IReadOnlyList<EnvironmentReadingStatus> Readings);

/// <summary>
/// Simple correlation (Pearson r over daily means) of a vision indicator with the CO2 of the same period.
/// <see cref="R"/> is null until there are enough days with both readings. A link in simulated data is not a cause.
/// </summary>
public sealed record Co2Correlation(
    string IndicatorId,
    decimal? R,
    int Pairs,
    CorrelationStrength Strength,
    string Explanation);

/// <summary>
/// GET /api/mc/v1/crew/{id}/vision (SANS, the red risk of NASA for Mars): IOP and visual acuity with their trend and
/// their correlation with CO2. Simulated, heuristic, not a diagnosis.
/// </summary>
public sealed record VisionSummaryDto(
    HealthStatus Status,
    IndicatorTrend Iop,
    IndicatorTrend VisualAcuity,
    Co2Correlation IopVsCo2,
    Co2Correlation VisualAcuityVsCo2,
    CatalogStatus CatalogStatus,
    SourceLink Source,
    DateTimeOffset? From,
    DateTimeOffset? To,
    bool Simulated = true);
