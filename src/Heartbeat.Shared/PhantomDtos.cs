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