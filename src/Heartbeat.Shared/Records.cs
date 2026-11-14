namespace Heartbeat.Shared;

public sealed record ReadingInput(
    IndicatorType Indicator,
    decimal Value,
    decimal? SecondaryValue = null);

public sealed record ReadingEvaluation(
    IndicatorType Indicator,
    HealthStatus Status,
    IReadOnlyList<string> BandIds,
    string Label,
    IReadOnlyList<string> Guidance,
    Escalation? Escalation,
    IReadOnlyList<SourceLink> Sources);

public sealed record Escalation(
    EscalationContact Contact,
    string Message);

public sealed record SourceLink(
    string Title,
    Uri Url);

public sealed record CheckUpEvaluation(
    HealthStatus OverallStatus,
    IReadOnlyList<ReadingEvaluation> Readings,
    string RangesVersion);