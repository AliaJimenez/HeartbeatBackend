using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Heartbeat.Shared;

/// <summary>Body regions of the desktop body figure (same names as the desktop BodyRegion enum).</summary>
public enum PhantomRegion
{
    Height,
    Neck,
    Chest,
    Waist,
    Thigh,
    Calf
}

/// <summary>Simulated percentage change of one body region.</summary>
public sealed record PhantomRegionChange(PhantomRegion Region, decimal ChangePercent);

/// <summary>A phantom after a simulated stay: the changed profile plus the change per body-figure region.</summary>
public sealed record PhantomSimulation(
    PhantomProfile Phantom,
    decimal GravityG,
    int Weeks,
    decimal ExerciseHoursPerWeek,
    IReadOnlyList<PhantomRegionChange> Regions,
    CatalogStatus Status);

/// <summary>Parameters of the (heuristic) phantom model, read from contracts/indicators.catalog.json.</summary>
public sealed record PhantomModel(
    decimal BonePctPerWeek,
    decimal MusclePctPerWeek,
    decimal FluidShiftMaxPct,
    decimal IopRiseMaxMmHg,
    decimal FluidTauWeeks,
    decimal ExerciseHoursForMaxMitigation,
    decimal MaxExerciseMitigation,
    decimal FemaleBoneFactor,
    decimal AgeBoneFactorOver50,
    int MaxWeeks,
    CatalogStatus Status,
    IReadOnlyDictionary<PhantomRegion, (string From, decimal Factor)> RegionFactors);

/// <summary>
/// Phantom profiles and simulated physical changes (bone, muscle, fluids, IOP) by gravity and exercise.
/// Every number comes from the catalog and is <see cref="CatalogStatus.Heuristic"/>: the output is simulated
/// and is never a diagnosis.
/// </summary>
public static class Phantoms
{
    private static readonly Lazy<PhantomModel> _model = new(LoadModel);
    private static readonly Lazy<IReadOnlyList<PhantomProfile>> _profiles = new(LoadProfiles);

    public static PhantomModel Model => _model.Value;

    /// <summary>The catalog phantoms, before any simulated change.</summary>
    public static IReadOnlyList<PhantomProfile> Profiles => _profiles.Value;

    public static PhantomProfile? Find(string idOrName) =>
        Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase) || p.Id == StableId(idOrName));

    /// <summary>
    /// Simulates <paramref name="weeks"/> weeks at <paramref name="gravityG"/> with
    /// <paramref name="exerciseHoursPerWeek"/> of countermeasure exercise. Deterministic: same input, same output.
    /// </summary>
    public static PhantomSimulation Simulate(PhantomProfile phantom, decimal gravityG, int weeks, decimal exerciseHoursPerWeek)
    {
        ArgumentNullException.ThrowIfNull(phantom);
        var model = Model;

        weeks = Math.Clamp(weeks, 0, model.MaxWeeks);
        gravityG = Math.Max(0m, gravityG);
        exerciseHoursPerWeek = Math.Max(0m, exerciseHoursPerWeek);

        // 0 g -> full effect, 1 g (or more) -> none.
        var gravityFactor = Math.Max(0m, 1m - gravityG);

        var mitigation = model.ExerciseHoursForMaxMitigation <= 0
            ? 0m
            : Math.Min(1m, exerciseHoursPerWeek / model.ExerciseHoursForMaxMitigation) * model.MaxExerciseMitigation;

        var boneFactor = 1m;
        if (string.Equals(phantom.Sex, "female", StringComparison.OrdinalIgnoreCase)) boneFactor *= model.FemaleBoneFactor;
        if (phantom.AgeYears > 50) boneFactor *= model.AgeBoneFactorOver50;

        var bone = model.BonePctPerWeek * weeks * gravityFactor * boneFactor * (1m - mitigation);
        var muscle = model.MusclePctPerWeek * weeks * gravityFactor * (1m - mitigation);

        // Fluids and IOP shift towards the head and level off (exponential approach), exercise helps a little.
        var saturation = model.FluidTauWeeks <= 0
            ? 1m
            : 1m - (decimal)Math.Exp(-(double)weeks / (double)model.FluidTauWeeks);
        var fluid = model.FluidShiftMaxPct * saturation * gravityFactor * (1m - mitigation * 0.5m);
        var iop = model.IopRiseMaxMmHg * saturation * gravityFactor * (1m - mitigation * 0.5m);

        bone = Round(bone);
        muscle = Round(muscle);
        fluid = Round(fluid);
        iop = Round(iop);

        var changed = phantom with
        {
            BoneChangePercent = bone,
            MuscleChangePercent = muscle,
            FluidChangePercent = fluid,
            IopChangeMmHg = iop,
        };

        return new PhantomSimulation(changed, gravityG, weeks, exerciseHoursPerWeek, RegionChanges(model, muscle, fluid), model.Status);
    }

    private static IReadOnlyList<PhantomRegionChange> RegionChanges(PhantomModel model, decimal muscle, decimal fluid)
        => Enum.GetValues<PhantomRegion>()
            .Select(region =>
            {
                var (from, factor) = model.RegionFactors.TryGetValue(region, out var f) ? f : ("muscle", 0m);
                var source = string.Equals(from, "fluid", StringComparison.OrdinalIgnoreCase) ? fluid : muscle;
                return new PhantomRegionChange(region, Round(source * factor));
            })
            .ToList();

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>A stable Guid for a catalog id, so the same phantom has the same Id on every run and device.</summary>
    public static Guid StableId(string catalogId)
        => new(MD5.HashData(Encoding.UTF8.GetBytes(catalogId)));

    private static PhantomModel LoadModel()
    {
        using var doc = IndicatorsCatalog.Parse();
        var m = doc.RootElement.GetProperty("phantomModel");

        var factors = new Dictionary<PhantomRegion, (string From, decimal Factor)>();
        foreach (var p in m.GetProperty("regionFactors").EnumerateObject())
        {
            var region = Enum.Parse<PhantomRegion>(p.Name, ignoreCase: true);
            factors[region] = (p.Value.GetProperty("from").GetString()!, p.Value.GetProperty("factor").GetDecimal());
        }

        foreach (var region in Enum.GetValues<PhantomRegion>())
        {
            if (!factors.ContainsKey(region))
            {
                throw new InvalidOperationException($"The phantom model has no factor for the region '{region}'.");
            }
        }

        var status = Enum.Parse<CatalogStatus>(m.GetProperty("status").GetString()!, ignoreCase: true);
        var source = m.GetProperty("source").GetString()!;
        if (!doc.RootElement.GetProperty("sources").TryGetProperty(source, out _))
        {
            throw new InvalidOperationException($"The phantom model references an unknown source '{source}'.");
        }

        return new PhantomModel(
            m.GetProperty("bonePctPerWeek").GetDecimal(),
            m.GetProperty("musclePctPerWeek").GetDecimal(),
            m.GetProperty("fluidShiftMaxPct").GetDecimal(),
            m.GetProperty("iopRiseMaxMmHg").GetDecimal(),
            m.GetProperty("fluidTauWeeks").GetDecimal(),
            m.GetProperty("exerciseHoursForMaxMitigation").GetDecimal(),
            m.GetProperty("maxExerciseMitigation").GetDecimal(),
            m.GetProperty("femaleBoneFactor").GetDecimal(),
            m.GetProperty("ageBoneFactorOver50").GetDecimal(),
            m.GetProperty("maxWeeks").GetInt32(),
            status,
            factors);
    }

    private static IReadOnlyList<PhantomProfile> LoadProfiles()
    {
        using var doc = IndicatorsCatalog.Parse();
        var list = new List<PhantomProfile>();
        foreach (var p in doc.RootElement.GetProperty("phantoms").EnumerateArray())
        {
            list.Add(new PhantomProfile(
                StableId(p.GetProperty("id").GetString()!),
                p.GetProperty("name").GetString()!,
                p.GetProperty("sex").GetString()!,
                p.GetProperty("ageYears").GetInt32(),
                p.GetProperty("massKg").GetDecimal()));
        }

        if (list.Count == 0)
        {
            throw new InvalidOperationException("The phantom catalog is empty.");
        }

        return list;
    }
}
