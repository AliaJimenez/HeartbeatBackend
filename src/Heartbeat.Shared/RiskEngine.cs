using System.Text.Json;

namespace Heartbeat.Shared;

/// <summary>
/// Everything the risk engine looks at, already evaluated by the piece that owns it: the latest vitals
/// (<see cref="HealthEvaluator"/>), the radiation summary, the latest habitat readings, the mission profile and
/// the isolation and confinement indices. Any of them can be missing (a new astronaut, an offline desktop).
/// </summary>
public sealed record RiskInputs(
    CheckUpEvaluation? Vitals = null,
    RadiationSummaryDto? Radiation = null,
    IReadOnlyList<EnvironmentReadingStatus>? Environment = null,
    EnvironmentProfile? Profile = null,
    IsolationConfinementIndices? Isolation = null,
    IReadOnlyList<PersonalDeviation>? Deviations = null,
    IReadOnlyList<EnvironmentReadingStatus>? Vision = null,    // latest IOP and visual acuity (HB-BE-05)
    DomainProfile? Domain = null);                             // commercial profile (HB-BE-11): its thresholds replace the catalog's

/// <summary>
/// HB-BE-07: one green / yellow / red per NASA hazard and one global (the worst), each with a plain-language reason
/// and a source. Pure and deterministic and lives in Heartbeat.Shared like <see cref="HealthEvaluator"/>, so the
/// desktop computes exactly the same result offline. Thresholds are never here: they come from the catalogs
/// (<c>reference-ranges.json</c>, <c>indicators.catalog.json</c>) and the radiation limits in <see cref="RadiationDoses"/>.
/// Data is simulated and the wording is not a diagnosis.
/// </summary>
public static class RiskEngine
{
    /// <summary>
    /// Gravity and distance from Earth come from the mission profile: they are the planned conditions of the mission,
    /// not something that went wrong, so they can warn (watch) but never turn the astronaut red on their own.
    /// Measured data (vitals, radiation, habitat readings, isolation indices) is not capped.
    /// </summary>
    public const HealthStatus PlannedConditionCap = HealthStatus.Watch;

    /// <summary>
    /// With <see cref="RiskInputs.Domain"/> the habitat and vision readings, the isolation indices and the planned
    /// gravity and delay are judged with that profile's thresholds. Vitals (reference-ranges.json) and radiation
    /// limits are not profile thresholds and stay the same. The inputs are not changed: readings are re-judged here.
    /// </summary>
    public static RiskStatus Evaluate(RiskInputs inputs)
    {
        var vitals = VitalFindings(inputs.Vitals);
        var domain = inputs.Domain;
        var environment = inputs.Environment?.Select(r => DomainProfiles.Apply(domain, r)).ToList();
        var vision = inputs.Vision?.Select(r => DomainProfiles.Apply(domain, r)).ToList();

        var hazards = new List<HazardStatus>
        {
            Radiation(inputs.Radiation, vitals),
            IsolationAndConfinement(inputs.Isolation, vitals, domain),
            DistanceFromEarth(inputs.Profile, vitals, domain),
            GravityFields(inputs.Profile, vision, vitals, domain),
            HostileClosedEnvironment(environment, vitals, domain),
        };
        hazards.Sort((a, b) => a.Hazard.CompareTo(b.Hazard));

        var status = hazards.Max(h => h.Status);
        return new RiskStatus(status, GlobalExplanation(hazards, status), hazards, inputs.Deviations, inputs.Isolation, domain?.Id);
    }

    public static string Name(Hazard hazard) => hazard switch
    {
        Hazard.SpaceRadiation => "Space radiation",
        Hazard.IsolationAndConfinement => "Isolation and confinement",
        Hazard.DistanceFromEarth => "Distance from Earth",
        Hazard.GravityFields => "Gravity fields",
        _ => "Hostile closed environment",
    };

    public static string Word(HealthStatus status) => status switch
    {
        HealthStatus.Normal => "normal",
        HealthStatus.Watch => "watch",
        _ => "critical",
    };

    /// <summary>The hazard a vital sign belongs to, as declared in <c>reference-ranges.json</c>.</summary>
    public static Hazard HazardOf(IndicatorType indicator)
        => VitalHazards.Value.TryGetValue(indicator, out var hazard) ? hazard : Hazard.GravityFields;

    // ---- findings -------------------------------------------------------------------------------------------------

    private sealed record Finding(
        string Name,
        HealthStatus Status,
        string Text,
        decimal? Value = null,
        decimal? Limit = null,
        string? Unit = null,
        SourceLink? Source = null);

    private static readonly Lazy<Dictionary<IndicatorType, Hazard>> VitalHazards = new(() =>
    {
        using var doc = ReferenceRanges.Parse();
        var map = new Dictionary<IndicatorType, Hazard>();
        foreach (var ind in doc.RootElement.GetProperty("indicators").EnumerateArray())
        {
            if (Enum.TryParse<IndicatorType>(ind.GetProperty("id").GetString(), ignoreCase: true, out var type)
                && ind.TryGetProperty("hazard", out var hazard)
                && Enum.TryParse<Hazard>(hazard.GetString(), ignoreCase: true, out var h))
            {
                map[type] = h;
            }
        }

        return map;
    });

    private static string VitalName(IndicatorType type) => type switch
    {
        IndicatorType.BloodPressure => "Blood pressure",
        IndicatorType.HeartRate => "Heart rate",
        IndicatorType.BloodSugar => "Blood sugar",
        IndicatorType.BodyTemperature => "Body temperature",
        IndicatorType.Sleep => "Sleep",
        _ => "Mood",
    };

    private static Dictionary<Hazard, List<Finding>> VitalFindings(CheckUpEvaluation? vitals)
    {
        var byHazard = new Dictionary<Hazard, List<Finding>>();
        foreach (var reading in vitals?.Readings ?? [])
        {
            var name = VitalName(reading.Indicator);
            var text = reading.Status == HealthStatus.Normal
                ? $"{name} is {reading.Label.ToLowerInvariant()}."
                : $"{name} is {reading.Label.ToLowerInvariant()}." + (reading.Guidance.Count > 0 ? $" {reading.Guidance[0]}" : string.Empty);

            var hazard = HazardOf(reading.Indicator);
            if (!byHazard.TryGetValue(hazard, out var list))
            {
                byHazard[hazard] = list = [];
            }

            list.Add(new Finding(name, reading.Status, text, Source: reading.Sources.FirstOrDefault()));
        }

        return byHazard;
    }

    private static IEnumerable<Finding> VitalsOf(Dictionary<Hazard, List<Finding>> vitals, Hazard hazard)
        => vitals.TryGetValue(hazard, out var list) ? list : [];

    // ---- one builder per hazard -----------------------------------------------------------------------------------

    private static HazardStatus Radiation(RadiationSummaryDto? radiation, Dictionary<Hazard, List<Finding>> vitals)
    {
        var findings = VitalsOf(vitals, Hazard.SpaceRadiation).ToList();
        string normal = "No radiation doses recorded yet (simulated data).";

        if (radiation is not null)
        {
            var careerStatus = RadiationDoses.StatusForCareerPercent(radiation.PercentOfLimit);
            var speStatus = RadiationDoses.StatusForSpePercent(radiation.PercentOfSpeLimit);
            var clauses = new List<string>();
            if (careerStatus != HealthStatus.Normal)
            {
                clauses.Add($"The accumulated dose is {radiation.PercentOfLimit:0.#}% of the {RadiationDoses.CareerLimitMsv:0} mSv career limit");
            }

            if (speStatus != HealthStatus.Normal)
            {
                clauses.Add($"the largest solar particle event is {radiation.PercentOfSpeLimit:0.#}% of the {RadiationDoses.SpeLimitMsv:0} mSv per-event limit");
            }

            normal = radiation.Entries == 0
                ? normal
                : $"The accumulated dose is {radiation.PercentOfLimit:0.#}% of the {RadiationDoses.CareerLimitMsv:0} mSv career limit (simulated data).";

            var status = RadiationDoses.WorstOf(careerStatus, speStatus);
            var text = status == HealthStatus.Normal
                ? normal
                : string.Join("; ", clauses) + ". Simulated doses, not a diagnosis.";
            findings.Add(new Finding("Radiation", status, UpperFirst(text), radiation.TotalMsv, RadiationDoses.CareerLimitMsv, "mSv", radiation.Source));
        }

        return Build(Hazard.SpaceRadiation, findings, normal);
    }

    private static HazardStatus IsolationAndConfinement(
        IsolationConfinementIndices? indices, Dictionary<Hazard, List<Finding>> vitals, DomainProfile? domain)
    {
        var findings = VitalsOf(vitals, Hazard.IsolationAndConfinement).ToList();
        if (indices is not null)
        {
            var hazard = IsolationConfinement.ToHazardStatus(indices, domain);
            findings.Add(new Finding("Isolation indices", hazard.Status, hazard.Explanation, hazard.Value, hazard.Limit, hazard.Unit, hazard.Source));
        }

        return Build(Hazard.IsolationAndConfinement, findings, "Sleep, mood and isolation are within their usual ranges.");
    }

    private static HazardStatus DistanceFromEarth(EnvironmentProfile? profile, Dictionary<Hazard, List<Finding>> vitals, DomainProfile? domain)
    {
        var findings = VitalsOf(vitals, Hazard.DistanceFromEarth).ToList();
        if (profile is not null && DomainProfiles.EntryFor(domain, "commDelay") is { } entry)
        {
            var delay = IsolationConfinement.CommDelayMinutes(profile.DistanceKm);
            var band = IndicatorsCatalog.Evaluate(entry, delay);
            var status = Cap(band.Status);
            var text = status == HealthStatus.Normal
                ? $"A message takes {delay:0.##} min to reach Earth, at {profile.DistanceKm:N0} km."
                : $"A one-way message takes {delay:0.#} min to reach Earth, at {profile.DistanceKm:N0} km. This is the planned distance of the mission, so plan for slower answers from the ground.";
            findings.Add(new Finding("Communication delay", status, text, Math.Round(delay, 2), NormalEdge(entry, delay), entry.Unit, IndicatorsCatalog.SourceFor(entry)));
        }

        return Build(Hazard.DistanceFromEarth, findings, "The delay to Earth is short (simulated data).");
    }

    private static HazardStatus GravityFields(
        EnvironmentProfile? profile, IReadOnlyList<EnvironmentReadingStatus>? vision, Dictionary<Hazard, List<Finding>> vitals, DomainProfile? domain)
    {
        var findings = VitalsOf(vitals, Hazard.GravityFields).ToList();
        if (profile is not null && DomainProfiles.EntryFor(domain, "gravity") is { } entry)
        {
            var band = IndicatorsCatalog.Evaluate(entry, profile.GravityG);
            var status = Cap(band.Status);
            var text = status == HealthStatus.Normal
                ? $"Gravity is {profile.GravityG:0.##} g (simulated data)."
                : $"Gravity is {profile.GravityG:0.##} g, below Earth's. This is the planned condition of the mission: keep up your exercise countermeasures and check your blood pressure and heart rate regularly.";
            findings.Add(new Finding("Gravity", status, text, profile.GravityG, NormalEdge(entry, profile.GravityG), entry.Unit, IndicatorsCatalog.SourceFor(entry)));
        }

        foreach (var r in vision ?? [])
        {
            var limit = DomainProfiles.EntryFor(domain, r.IndicatorId) is { } e ? NormalEdge(e, r.Value) : null;
            var text = r.Status == HealthStatus.Normal
                ? $"{r.Name} is {r.Value:0.##} {r.Unit}."
                : $"{r.Name} is {r.Label.ToLowerInvariant()}: {r.Value:0.##} {r.Unit} (simulated reading).";
            findings.Add(new Finding(r.Name, r.Status, text, r.Value, limit, r.Unit, r.Source));
        }

        return Build(Hazard.GravityFields, findings, "Blood pressure, heart rate, eye readings and gravity are within their usual ranges.");
    }

    private static HazardStatus HostileClosedEnvironment(
        IReadOnlyList<EnvironmentReadingStatus>? readings, Dictionary<Hazard, List<Finding>> vitals, DomainProfile? domain)
    {
        var findings = VitalsOf(vitals, Hazard.HostileClosedEnvironment).ToList();
        foreach (var r in readings ?? [])
        {
            var entry = DomainProfiles.EntryFor(domain, r.IndicatorId);
            var limit = entry is null ? null : NormalEdge(entry, r.Value);
            var text = r.Status == HealthStatus.Normal
                ? $"{r.Name} is {r.Value:0.##} {r.Unit}."
                : $"{r.Name} is {r.Label.ToLowerInvariant()}: {r.Value:0.##} {r.Unit} (simulated reading).";
            findings.Add(new Finding(r.Name, r.Status, text, r.Value, limit, r.Unit, r.Source));
        }

        return Build(Hazard.HostileClosedEnvironment, findings, "The habitat readings and the vitals tied to it are within their usual ranges.");
    }

    // ---- shared helpers -------------------------------------------------------------------------------------------

    private static HazardStatus Build(Hazard hazard, List<Finding> findings, string normalText)
    {
        var status = findings.Select(f => f.Status).DefaultIfEmpty(HealthStatus.Normal).Max();
        if (status == HealthStatus.Normal)
        {
            // Nothing to warn about: point at the most informative finding (the one with a value) for the numbers.
            var info = findings.FirstOrDefault(f => f.Value is not null);
            return new HazardStatus(hazard, status, normalText, info?.Value, info?.Limit, info?.Unit, info?.Source ?? findings.FirstOrDefault()?.Source);
        }

        var flagged = findings.Where(f => f.Status != HealthStatus.Normal).OrderByDescending(f => f.Status).ToList();
        var worst = flagged[0];
        var explanation = string.Join(" ", flagged.Take(3).Select(f => EnsureSentence(f.Text)));
        return new HazardStatus(hazard, status, explanation, worst.Value, worst.Limit, worst.Unit, worst.Source ?? findings.Select(f => f.Source).FirstOrDefault(s => s is not null));
    }

    private static string GlobalExplanation(IReadOnlyList<HazardStatus> hazards, HealthStatus status)
    {
        if (status == HealthStatus.Normal)
        {
            return "All five hazards are normal.";
        }

        var ordered = hazards.Where(h => h.Status != HealthStatus.Normal).OrderByDescending(h => h.Status).ThenBy(h => h.Hazard).ToList();
        var lead = ordered[0];
        var text = $"{Name(lead.Hazard)} is {Word(lead.Status)}: {lead.Explanation}";
        if (ordered.Count > 1)
        {
            text += " Also: " + string.Join(", ", ordered.Skip(1).Select(h => $"{Name(h.Hazard).ToLowerInvariant()} ({Word(h.Status)})")) + ".";
        }

        return text;
    }

    private static HealthStatus Cap(HealthStatus status) => status > PlannedConditionCap ? PlannedConditionCap : status;

    /// <summary>The edge of the normal band a value has crossed (its upper edge above it, its lower edge below it).</summary>
    private static decimal? NormalEdge(IndicatorCatalogEntry entry, decimal value)
    {
        var normal = entry.Bands.FirstOrDefault(b => b.Status == HealthStatus.Normal);
        if (normal is null)
        {
            return null;
        }

        if (normal.Max is { } max && value >= max)
        {
            return max;
        }

        return normal.Min is { } min && value < min ? min : null;
    }

    private static string EnsureSentence(string text)
        => text.EndsWith('.') || text.EndsWith('!') ? text : text + ".";

    private static string UpperFirst(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
