namespace Heartbeat.Shared;

/// <summary>Defaults and limits of the phantom comparison, read from <c>phantomModel.compare</c> in the catalog.</summary>
public sealed record PhantomCompareDefaults(
    string Environment,
    int Weeks,
    decimal ExerciseHoursPerWeek,
    decimal MaxExerciseHoursPerWeek);

/// <summary>
/// HB-BE-08: the same simulated stay for several phantoms side by side. Pure and deterministic, in Heartbeat.Shared
/// like <see cref="Phantoms"/>, so the portal and the desktop can build the same series. Environments (gravity), defaults
/// and every model number come from <c>contracts/indicators.catalog.json</c>; the output is simulated and heuristic.
/// </summary>
public static class PhantomComparison
{
    public const string Note =
        "Simulated, heuristic estimates for a digital phantom (not a real person). They are not a prediction and not a diagnosis.";

    private static readonly Lazy<PhantomCompareDefaults> _defaults = new(LoadDefaults);
    private static readonly Lazy<IReadOnlyList<PhantomEnvironment>> _environments = new(LoadEnvironments);

    public static PhantomCompareDefaults Defaults => _defaults.Value;

    /// <summary>The environments of <c>phantomModel.environments</c> (station, spacecraft, lunarBase, marsBase).</summary>
    public static IReadOnlyList<PhantomEnvironment> Environments => _environments.Value;

    /// <summary>
    /// An environment by key (case-insensitive): one of <see cref="Environments"/>, or the id of a domain profile
    /// (<c>profiles</c> in the catalog, for example <c>mining</c> at 1 g, where nothing changes).
    /// </summary>
    public static PhantomEnvironment? FindEnvironment(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var environment = Environments.FirstOrDefault(e => string.Equals(e.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
        if (environment is not null)
        {
            return environment;
        }

        var profile = DomainProfiles.Find(key.Trim());
        return profile is null ? null : new PhantomEnvironment(profile.Id, profile.Name, profile.Environment.GravityG);
    }

    /// <summary>
    /// A phantom by catalog id, name or Guid (case-insensitive), or null.
    /// </summary>
    public static PhantomProfile? FindPhantom(string? idNameOrGuid)
    {
        if (string.IsNullOrWhiteSpace(idNameOrGuid))
        {
            return null;
        }

        var key = idNameOrGuid.Trim();
        return Phantoms.Find(key)
            ?? (Guid.TryParse(key, out var guid) ? Phantoms.Profiles.FirstOrDefault(p => p.Id == guid) : null);
    }

    /// <summary>
    /// Simulates the stay week by week for each phantom (weeks 0 to <paramref name="weeks"/>). The weeks are not clamped
    /// here: callers validate them against <see cref="PhantomModel.MaxWeeks"/>.
    /// </summary>
    public static PhantomComparisonDto Compare(
        IEnumerable<PhantomProfile> phantoms,
        PhantomEnvironment environment,
        int weeks,
        decimal exerciseHoursPerWeek)
    {
        ArgumentNullException.ThrowIfNull(environment);
        weeks = Math.Clamp(weeks, 0, Phantoms.Model.MaxWeeks);

        var series = phantoms
            .DistinctBy(p => p.Id)
            .Select(phantom =>
            {
                var points = Enumerable.Range(0, weeks + 1)
                    .Select(week => Point(Phantoms.Simulate(phantom, environment.GravityG, week, exerciseHoursPerWeek), week))
                    .ToList();
                var last = Phantoms.Simulate(phantom, environment.GravityG, weeks, exerciseHoursPerWeek);
                return new PhantomSeries(phantom with { BoneChangePercent = 0m, MuscleChangePercent = 0m, FluidChangePercent = 0m, IopChangeMmHg = 0m },
                    points, points[^1], last.Regions);
            })
            .ToList();

        return new PhantomComparisonDto(
            environment.Key,
            environment.Name,
            environment.GravityG,
            weeks,
            exerciseHoursPerWeek,
            Phantoms.Model.Status,
            ReferenceSource(),
            series,
            Note);
    }

    private static PhantomPoint Point(PhantomSimulation s, int week)
        => new(week, s.Phantom.BoneChangePercent, s.Phantom.MuscleChangePercent, s.Phantom.FluidChangePercent, s.Phantom.IopChangeMmHg);

    private static SourceLink ReferenceSource()
    {
        using var doc = IndicatorsCatalog.Parse();
        var source = doc.RootElement.GetProperty("phantomModel").GetProperty("source").GetString()!;
        var entry = doc.RootElement.GetProperty("sources").GetProperty(source);
        return new SourceLink(entry.GetProperty("title").GetString()!, new Uri(entry.GetProperty("url").GetString()!));
    }

    private static PhantomCompareDefaults LoadDefaults()
    {
        using var doc = IndicatorsCatalog.Parse();
        var c = doc.RootElement.GetProperty("phantomModel").GetProperty("compare");
        var defaults = new PhantomCompareDefaults(
            c.GetProperty("defaultEnvironment").GetString()!,
            c.GetProperty("defaultWeeks").GetInt32(),
            c.GetProperty("defaultExerciseHoursPerWeek").GetDecimal(),
            c.GetProperty("maxExerciseHoursPerWeek").GetDecimal());

        if (defaults.Weeks < 1 || defaults.Weeks > Phantoms.Model.MaxWeeks)
        {
            throw new InvalidOperationException("The default comparison weeks are outside 1 to the model maximum.");
        }

        return defaults;
    }

    private static IReadOnlyList<PhantomEnvironment> LoadEnvironments()
    {
        using var doc = IndicatorsCatalog.Parse();
        var list = doc.RootElement.GetProperty("phantomModel").GetProperty("environments").EnumerateObject()
            .Select(p => new PhantomEnvironment(p.Name, p.Value.GetProperty("name").GetString()!, p.Value.GetProperty("gravityG").GetDecimal()))
            .ToList();

        if (list.Count == 0)
        {
            throw new InvalidOperationException("The phantom model has no environments.");
        }

        return list;
    }
}
