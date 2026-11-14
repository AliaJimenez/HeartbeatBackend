using System.Text.Json;
using Heartbeat.Shared;

namespace Heartbeat.Tests;

/// <summary>
/// SH-TESTS (MR-03): protects the evaluation rules when someone edits contracts/reference-ranges.json.
/// Band cases are generated from the embedded JSON itself, so a new or moved limit is tested automatically.
/// </summary>
public class HealthEvaluatorBoundaryTests
{
    private const decimal JustBelow = 0.01m;
    private static readonly HealthEvaluator Eval = new();

    // A value that is "normal" for the other half of a blood pressure pair, so only the probed component decides.
    private const decimal NormalSystolic = 110m;
    private const decimal NormalDiastolic = 70m;

    public sealed record BandInfo(IndicatorType Indicator, string? Component, string Id, HealthStatus Status, decimal? Min, decimal? Max);

    private static IEnumerable<BandInfo> AllBands()
    {
        using var doc = ReferenceRanges.Parse();
        var list = new List<BandInfo>();
        foreach (var ind in doc.RootElement.GetProperty("indicators").EnumerateArray())
        {
            var type = Enum.Parse<IndicatorType>(ind.GetProperty("id").GetString()!, ignoreCase: true);
            if (ind.TryGetProperty("bands", out var bands))
            {
                list.AddRange(bands.EnumerateArray().Select(b => ToInfo(type, null, b)));
            }
            else
            {
                foreach (var component in ind.GetProperty("components").EnumerateObject())
                {
                    list.AddRange(component.Value.EnumerateArray().Select(b => ToInfo(type, component.Name, b)));
                }
            }
        }

        return list;
    }

    private static BandInfo ToInfo(IndicatorType type, string? component, JsonElement b) => new(
        type,
        component,
        b.GetProperty("id").GetString()!,
        Enum.Parse<HealthStatus>(b.GetProperty("status").GetString()!, ignoreCase: true),
        b.TryGetProperty("min", out var min) ? min.GetDecimal() : null,
        b.TryGetProperty("max", out var max) ? max.GetDecimal() : null);

    private static ReadingInput Probe(BandInfo band, decimal value) => band.Component switch
    {
        "value" => new ReadingInput(band.Indicator, value, NormalDiastolic),
        "secondaryValue" => new ReadingInput(band.Indicator, NormalSystolic, value),
        _ => new ReadingInput(band.Indicator, value),
    };

    private static string Describe(BandInfo b) => $"{b.Id} [{b.Min}, {b.Max})";

    public static IEnumerable<object[]> LowerLimits()
        => AllBands().Where(b => b.Min is not null).Select(b => new object[] { Describe(b), b });

    public static IEnumerable<object[]> UpperLimits()
        => AllBands().Where(b => b.Max is not null).Select(b => new object[] { Describe(b), b });

    [Theory]
    [MemberData(nameof(LowerLimits))]
    public void Value_exactly_at_min_belongs_to_the_band(string name, BandInfo band)
    {
        var result = Eval.Evaluate(Probe(band, band.Min!.Value));

        Assert.Contains(band.Id, result.BandIds);
        // Only this component can be non-normal, so the reading status is the band status.
        Assert.Equal(band.Status, result.Status);
    }

    [Theory]
    [MemberData(nameof(UpperLimits))]
    public void Value_just_below_max_still_belongs_to_the_band(string name, BandInfo band)
    {
        var result = Eval.Evaluate(Probe(band, band.Max!.Value - JustBelow));

        Assert.Contains(band.Id, result.BandIds);
        Assert.Equal(band.Status, result.Status);
    }

    [Theory]
    [MemberData(nameof(UpperLimits))]
    public void Value_exactly_at_max_leaves_the_band(string name, BandInfo band)
    {
        var result = Eval.Evaluate(Probe(band, band.Max!.Value));

        Assert.DoesNotContain(band.Id, result.BandIds);
    }

    [Fact]
    public void Every_band_has_guidance_and_non_normal_bands_escalate()
    {
        foreach (var band in AllBands())
        {
            var min = band.Min ?? band.Max!.Value - 1m;
            var result = Eval.Evaluate(Probe(band, min));

            if (band.Status != HealthStatus.Normal)
            {
                Assert.NotEmpty(result.Guidance);            // MR-04
                Assert.NotNull(result.Escalation);
            }

            if (band.Status == HealthStatus.Critical)
            {
                Assert.Equal(EscalationContact.CrewMedicalOfficer, result.Escalation!.Contact); // MR-06
            }
        }
    }

    // ----- Reference file integrity: no gaps, no overlaps -----

    public static IEnumerable<object[]> BandGroups()
        => AllBands().GroupBy(b => (b.Indicator, b.Component)).Select(g => new object[] { $"{g.Key.Indicator}/{g.Key.Component ?? "value"}", g.Select(b => b).ToList() });

    [Theory]
    [MemberData(nameof(BandGroups))]
    public void Bands_cover_the_whole_line_without_gaps_or_overlaps(string name, List<BandInfo> bands)
    {
        var ordered = bands.OrderBy(b => b.Min ?? decimal.MinValue).ToList();

        Assert.Null(ordered[0].Min);                    // open at the bottom
        Assert.Null(ordered[^1].Max);                   // open at the top
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            Assert.True(ordered[i].Max is not null, $"{ordered[i].Id} must have a max because {ordered[i + 1].Id} follows it");
            Assert.Equal(ordered[i].Max, ordered[i + 1].Min);   // contiguous: no gap, no overlap
        }

        Assert.Equal(bands.Count, bands.Select(b => b.Id).Distinct().Count());
    }

    [Fact]
    public void Every_indicator_has_bands_and_each_enum_value_is_covered()
    {
        var covered = AllBands().Select(b => b.Indicator).Distinct().ToHashSet();
        foreach (var type in Enum.GetValues<IndicatorType>())
        {
            Assert.Contains(type, covered);
        }
    }

    [Fact]
    public void Every_band_status_and_source_reference_in_the_file_is_known()
    {
        using var doc = ReferenceRanges.Parse();
        var sources = doc.RootElement.GetProperty("sources").EnumerateObject().Select(p => p.Name).ToHashSet();
        foreach (var ind in doc.RootElement.GetProperty("indicators").EnumerateArray())
        {
            foreach (var s in ind.GetProperty("sources").EnumerateArray())
            {
                Assert.Contains(s.GetString(), sources);
            }
        }

        Assert.All(AllBands(), b => Assert.True(Enum.IsDefined(b.Status)));
    }

    // ----- Blood pressure: the worse component wins -----

    [Fact]
    public void Diastolic_wins_when_it_is_worse_than_systolic()
    {
        var r = Eval.Evaluate(new ReadingInput(IndicatorType.BloodPressure, 120m, 85m));   // sys normal, dia watch-high

        Assert.Equal(HealthStatus.Watch, r.Status);
        Assert.Equal(["bp-sys-normal", "bp-dia-watch-high"], r.BandIds);
        Assert.Equal("High diastolic", r.Label);
    }

    [Fact]
    public void Diastolic_critical_beats_systolic_watch()
    {
        var r = Eval.Evaluate(new ReadingInput(IndicatorType.BloodPressure, 140m, 125m));  // sys watch-high, dia critical-high

        Assert.Equal(HealthStatus.Critical, r.Status);
        Assert.Equal("Very high diastolic", r.Label);
        Assert.Equal(EscalationContact.CrewMedicalOfficer, r.Escalation!.Contact);
    }

    [Fact]
    public void Diastolic_low_beats_normal_systolic()
    {
        var r = Eval.Evaluate(new ReadingInput(IndicatorType.BloodPressure, 100m, 45m));   // dia critical-low

        Assert.Equal(HealthStatus.Critical, r.Status);
        Assert.Equal("Very low diastolic", r.Label);
    }

    [Fact]
    public void Systolic_wins_when_it_is_worse_than_diastolic()
    {
        var r = Eval.Evaluate(new ReadingInput(IndicatorType.BloodPressure, 185m, 95m));   // sys critical, dia watch

        Assert.Equal(HealthStatus.Critical, r.Status);
        Assert.Equal("Very high systolic", r.Label);
        Assert.Equal(["bp-sys-critical-high", "bp-dia-watch-high"], r.BandIds);
    }

    [Fact]
    public void Same_status_on_both_components_reports_both_bands_and_systolic_label()
    {
        var r = Eval.Evaluate(new ReadingInput(IndicatorType.BloodPressure, 150m, 90m));   // both watch-high

        Assert.Equal(HealthStatus.Watch, r.Status);
        Assert.Equal("High systolic", r.Label);
        Assert.Equal(2, r.BandIds.Count);
    }

    [Fact]
    public void Guidance_comes_only_from_the_worst_bands()
    {
        // MR-04 / CONTRACTS.md §2 rule 5: a normal half must not dilute the advice with "no action needed".
        var r = Eval.Evaluate(new ReadingInput(IndicatorType.BloodPressure, 120m, 85m));

        Assert.NotEmpty(r.Guidance);
        Assert.DoesNotContain("No action needed.", r.Guidance);
        Assert.DoesNotContain("Keep up your exercise countermeasures and hydration.", r.Guidance);
        Assert.Equal(r.Guidance.Count, r.Guidance.Distinct().Count());
    }

    [Fact]
    public void Both_normal_gives_normal_without_escalation()
    {
        var r = Eval.Evaluate(new ReadingInput(IndicatorType.BloodPressure, 90m, 60m));    // both exactly at their normal min

        Assert.Equal(HealthStatus.Normal, r.Status);
        Assert.Null(r.Escalation);
    }

    // ----- Check-up level -----

    [Fact]
    public void Check_up_status_is_the_worst_reading()
    {
        var result = Eval.Evaluate([
            new ReadingInput(IndicatorType.HeartRate, 70m),                 // normal
            new ReadingInput(IndicatorType.Sleep, 5m),                      // watch
            new ReadingInput(IndicatorType.BodyTemperature, 36.6m)]);       // normal

        Assert.Equal(HealthStatus.Watch, result.OverallStatus);
        Assert.Equal(3, result.Readings.Count);
        Assert.Equal(Eval.RangesVersion, result.RangesVersion);
    }

    [Fact]
    public void Evaluation_is_deterministic()
    {
        var input = new ReadingInput(IndicatorType.BloodSugar, 141m);
        Assert.Equivalent(Eval.Evaluate(input), Eval.Evaluate(input));
    }

    // ----- Validation (rule 1: outside validRange is a typing error, not a status) -----

    public sealed record RangeCase(IndicatorType Indicator, string Component, decimal Min, decimal Max);

    public static IEnumerable<object[]> ValidRanges()
    {
        using var doc = ReferenceRanges.Parse();
        foreach (var ind in doc.RootElement.GetProperty("indicators").EnumerateArray())
        {
            var type = Enum.Parse<IndicatorType>(ind.GetProperty("id").GetString()!, ignoreCase: true);
            foreach (var range in ind.GetProperty("validRange").EnumerateObject())
            {
                var c = new RangeCase(type, range.Name, range.Value.GetProperty("min").GetDecimal(), range.Value.GetProperty("max").GetDecimal());
                yield return [$"{type}/{range.Name}", c];
            }
        }
    }

    private static ReadingInput WithComponent(RangeCase c, decimal value) => c.Component == "secondaryValue"
        ? new ReadingInput(c.Indicator, NormalSystolic, value)
        : new ReadingInput(c.Indicator, value, c.Indicator == IndicatorType.BloodPressure ? NormalDiastolic : null);

    [Theory]
    [MemberData(nameof(ValidRanges))]
    public void Valid_range_limits_are_accepted(string name, RangeCase c)
    {
        Assert.Empty(Eval.Validate(WithComponent(c, c.Min)));
        Assert.Empty(Eval.Validate(WithComponent(c, c.Max)));
    }

    [Theory]
    [MemberData(nameof(ValidRanges))]
    public void Values_just_outside_the_valid_range_are_rejected(string name, RangeCase c)
    {
        Assert.NotEmpty(Eval.Validate(WithComponent(c, c.Min - JustBelow)));
        Assert.NotEmpty(Eval.Validate(WithComponent(c, c.Max + JustBelow)));
    }

    [Fact]
    public void Blood_pressure_without_diastolic_is_invalid()
    {
        var errors = Eval.Validate(new ReadingInput(IndicatorType.BloodPressure, 120m));

        Assert.Contains(errors, e => e.Contains("SecondaryValue", StringComparison.Ordinal));
    }

    [Fact]
    public void Blood_pressure_reports_which_component_is_out_of_range()
    {
        var errors = Eval.Validate(new ReadingInput(IndicatorType.BloodPressure, 300m, 20m));

        Assert.Contains(errors, e => e.StartsWith("Value ", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("SecondaryValue ", StringComparison.Ordinal));
    }

    [Fact]
    public void Single_value_indicators_do_not_need_a_secondary_value()
    {
        Assert.Empty(Eval.Validate(new ReadingInput(IndicatorType.HeartRate, 70m)));
    }
}
