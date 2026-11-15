namespace Heartbeat.Shared;

/// <summary>
/// Digital phantom profile used for the (simulated) body-composition comparison.
/// All changes are simulated and flagged as such in the UI.
/// </summary>
public sealed record PhantomProfile(
    Guid Id,
    string Name,
    string Sex,
    int AgeYears,
    decimal MassKg,
    decimal BoneChangePercent = 0m,
    decimal MuscleChangePercent = 0m,
    decimal FluidChangePercent = 0m,
    decimal IopChangeMmHg = 0m)
{
    public string Gender => Sex;
    public int Age => AgeYears;
    public decimal Mass => MassKg;
}

/// <summary>The simulated change of one phantom at one week of the stay.</summary>
public sealed record PhantomPoint(
    int Week,
    decimal BoneChangePercent,
    decimal MuscleChangePercent,
    decimal FluidChangePercent,
    decimal IopChangeMmHg);

/// <summary>
/// One phantom in a comparison: the profile as it starts (no change), a point per week from 0 to the last week, the
/// last point and the change per body-figure region at the last week.
/// </summary>
public sealed record PhantomSeries(
    PhantomProfile Phantom,
    IReadOnlyList<PhantomPoint> Points,
    PhantomPoint Final,
    IReadOnlyList<PhantomRegionChange> Regions);

/// <summary>
/// GET /api/mc/v1/phantoms/compare: the same stay (environment, weeks, exercise) simulated for several phantoms, one
/// series each. Simulated, heuristic and never a diagnosis.
/// </summary>
public sealed record PhantomComparisonDto(
    string Environment,
    string EnvironmentName,
    decimal GravityG,
    int Weeks,
    decimal ExerciseHoursPerWeek,
    CatalogStatus Status,
    SourceLink Source,
    IReadOnlyList<PhantomSeries> Series,
    string Note,
    bool Simulated = true);

/// <summary>An environment a phantom can be compared in, as the catalog defines it.</summary>
public sealed record PhantomEnvironment(string Key, string Name, decimal GravityG);
