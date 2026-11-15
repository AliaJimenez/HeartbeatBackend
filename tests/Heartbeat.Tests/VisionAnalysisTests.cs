using Heartbeat.Shared;

namespace Heartbeat.Tests;

/// <summary>HB-BE-05 (#113): unit tests of the shared SANS analysis (trend and correlation with CO2).</summary>
public class VisionAnalysisTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static EnvironmentReadingStatus Reading(string id, decimal value, int day, int hour = 0)
    {
        var entry = IndicatorsCatalog.Find(id)!;
        var band = IndicatorsCatalog.Evaluate(entry, value);
        return new EnvironmentReadingStatus(
            Guid.NewGuid(), Day0.AddDays(day).AddHours(hour), null, entry.Id, entry.Name, value, null, entry.Unit,
            band.Status, string.IsNullOrWhiteSpace(band.Label) ? RiskEngine.Word(band.Status) : band.Label,
            entry.Status, IndicatorsCatalog.SourceFor(entry), null);
    }

    private static List<EnvironmentReadingStatus> Series(string id, params decimal[] values)
        => values.Select((v, i) => Reading(id, v, i)).ToList();

    private static List<VisionPoint> Co2(params decimal[] values)
        => values.Select((v, i) => new VisionPoint(Day0.AddDays(i).AddHours(3), v)).ToList();

    private static VisionSummaryDto Run(IEnumerable<EnvironmentReadingStatus>? iop = null, IEnumerable<EnvironmentReadingStatus>? acuity = null, IEnumerable<VisionPoint>? co2 = null)
        => VisionAnalysis.Analyze(iop ?? [], acuity ?? [], co2 ?? []);

    // ---- trend ----------------------------------------------------------------------------------------------------

    [Fact]
    public void No_readings_means_unknown_trend_normal_status_and_a_clear_message()
    {
        var s = Run();

        Assert.Equal(HealthStatus.Normal, s.Status);
        Assert.Equal(TrendDirection.Unknown, s.Iop.Direction);
        Assert.Equal(0, s.Iop.Count);
        Assert.Null(s.Iop.Latest);
        Assert.Contains("No Presión intraocular (IOP) readings", s.Iop.Explanation);
        Assert.True(s.Simulated);
    }

    [Fact]
    public void One_reading_has_a_latest_value_but_no_trend()
    {
        var s = Run(iop: Series("iop", 16m));

        Assert.Equal(TrendDirection.Unknown, s.Iop.Direction);
        Assert.Equal(16m, s.Iop.Latest);
        Assert.Contains("One reading is not enough", s.Iop.Explanation);
    }

    [Fact]
    public void A_rising_iop_reports_the_change_and_the_slope_per_day()
    {
        var s = Run(iop: Series("iop", 15m, 16m, 17m, 18m, 19m));

        Assert.Equal(TrendDirection.Rising, s.Iop.Direction);
        Assert.Equal(1m, s.Iop.SlopePerDay);
        Assert.Equal(4m, s.Iop.Change);
        Assert.Equal(19m, s.Iop.Latest);
        Assert.Contains("rising", s.Iop.Explanation);
        Assert.Contains("4 mmHg higher over 4 day(s)", s.Iop.Explanation);
    }

    [Fact]
    public void A_falling_visual_acuity_score_is_falling()
    {
        var s = Run(acuity: Series("visualAcuity", 0.3m, 0.2m, 0.1m, 0m));

        Assert.Equal(TrendDirection.Falling, s.VisualAcuity.Direction);
        Assert.Equal(-0.3m, s.VisualAcuity.Change);
    }

    [Theory]
    [InlineData(0.4, TrendDirection.Stable)]       // below the 0.5 mmHg rule of the catalog
    [InlineData(0.6, TrendDirection.Rising)]
    public void Stable_is_decided_by_the_catalog_rule(double rise, TrendDirection expected)
    {
        var s = Run(iop: [Reading("iop", 16m, 0), Reading("iop", 16m + (decimal)rise, 5)]);

        Assert.Equal(expected, s.Iop.Direction);
    }

    [Fact]
    public void The_trend_does_not_depend_on_the_order_the_readings_arrive_in()
    {
        var ordered = Series("iop", 15m, 17m, 19m);
        var shuffled = new[] { ordered[2], ordered[0], ordered[1] };

        Assert.Equal(Run(iop: ordered).Iop.SlopePerDay, Run(iop: shuffled).Iop.SlopePerDay);
        Assert.Equal(19m, Run(iop: shuffled).Iop.Latest);
    }

    [Fact]
    public void Readings_of_other_indicators_are_ignored()
    {
        var s = Run(iop: [Reading("iop", 16m, 0), Reading("co2", 4m, 1)]);

        Assert.Equal(1, s.Iop.Count);
    }

    [Theory]
    [InlineData(15, HealthStatus.Normal)]
    [InlineData(21, HealthStatus.Watch)]
    [InlineData(25, HealthStatus.Critical)]
    [InlineData(8, HealthStatus.Watch)]        // below the catalog's normal band: outside the usual range, not an error
    public void Status_comes_from_the_catalog_bands_of_the_latest_iop(decimal iop, HealthStatus expected)
    {
        var s = Run(iop: Series("iop", 15m, iop));

        Assert.Equal(expected, s.Iop.Status);
        Assert.Equal(expected, s.Status);
    }

    [Fact]
    public void Overall_status_is_the_worse_of_iop_and_visual_acuity()
    {
        var s = Run(iop: Series("iop", 15m), acuity: Series("visualAcuity", 0.6m));

        Assert.Equal(HealthStatus.Critical, s.Status);
        Assert.Equal(HealthStatus.Normal, s.Iop.Status);
        Assert.Equal(HealthStatus.Critical, s.VisualAcuity.Status);
    }

    [Fact]
    public void Sources_and_catalog_status_come_from_the_catalog()
    {
        var s = Run(iop: Series("iop", 15m, 16m));

        Assert.Equal(CatalogStatus.Heuristic, s.CatalogStatus);
        Assert.Equal(CatalogStatus.Heuristic, s.Iop.CatalogStatus);
        Assert.Contains("nasa.gov", s.Source.Url.ToString());
        Assert.Contains("not a diagnosis", s.Iop.Explanation);
    }

    // ---- correlation with CO2 -------------------------------------------------------------------------------------

    [Fact]
    public void Iop_that_follows_co2_has_a_strong_positive_correlation()
    {
        var s = Run(iop: Series("iop", 15m, 16m, 17m, 18m, 19m), co2: Co2(2m, 2.5m, 3m, 3.5m, 4m));

        Assert.Equal(1m, s.IopVsCo2.R);
        Assert.Equal(5, s.IopVsCo2.Pairs);
        Assert.Equal(CorrelationStrength.Strong, s.IopVsCo2.Strength);
        Assert.Contains("moved together with CO2", s.IopVsCo2.Explanation);
        Assert.Contains("does not show a cause", s.IopVsCo2.Explanation);
    }

    [Fact]
    public void An_opposite_movement_is_a_negative_correlation()
    {
        var s = Run(iop: Series("iop", 15m, 16m, 17m, 18m), co2: Co2(4m, 3m, 2m, 1m));

        Assert.Equal(-1m, s.IopVsCo2.R);
        Assert.Contains("moved opposite to CO2", s.IopVsCo2.Explanation);
    }

    [Fact]
    public void Unrelated_series_give_a_weak_correlation()
    {
        var s = Run(iop: Series("iop", 15m, 18m, 15m, 18m, 16.5m), co2: Co2(2m, 2m, 3m, 3m, 2.5m));

        Assert.Equal(CorrelationStrength.Weak, s.IopVsCo2.Strength);
        Assert.Contains("little relation", s.IopVsCo2.Explanation);
    }

    [Fact]
    public void A_moderate_correlation_sits_between_the_catalog_limits()
    {
        var s = Run(iop: Series("iop", 15m, 17m, 16m, 18m, 16.5m), co2: Co2(2m, 3.5m, 2.5m, 3m, 4m));

        Assert.Equal(CorrelationStrength.Moderate, s.IopVsCo2.Strength);
        Assert.InRange(s.IopVsCo2.R!.Value, 0.3m, 0.6m);
    }

    [Fact]
    public void Correlation_needs_enough_days_with_both_readings()
    {
        // 4 IOP days, but CO2 only on the first two.
        var s = Run(iop: Series("iop", 15m, 16m, 17m, 18m), co2: Co2(2m, 3m));

        Assert.Null(s.IopVsCo2.R);
        Assert.Equal(2, s.IopVsCo2.Pairs);
        Assert.Equal(CorrelationStrength.Unknown, s.IopVsCo2.Strength);
        Assert.Contains("Not enough days", s.IopVsCo2.Explanation);
    }

    [Fact]
    public void A_flat_series_has_no_correlation_to_compute()
    {
        var s = Run(iop: Series("iop", 16m, 16m, 16m, 16m), co2: Co2(2m, 3m, 4m, 5m));

        Assert.Null(s.IopVsCo2.R);
        Assert.Contains("did not vary", s.IopVsCo2.Explanation);
    }

    [Fact]
    public void Several_readings_on_one_day_count_as_their_daily_mean()
    {
        var iop = new[]
        {
            Reading("iop", 14m, 0, 0), Reading("iop", 16m, 0, 6),      // mean 15
            Reading("iop", 17m, 1), Reading("iop", 19m, 2),
        };
        var co2 = new[]
        {
            new VisionPoint(Day0.AddHours(2), 2m), new VisionPoint(Day0.AddDays(1).AddHours(2), 3m), new VisionPoint(Day0.AddDays(2).AddHours(2), 4m),
        };

        var s = Run(iop: iop, co2: co2);

        Assert.Equal(3, s.IopVsCo2.Pairs);
        Assert.True(s.IopVsCo2.R > 0.9m);
    }

    [Fact]
    public void Visual_acuity_is_correlated_with_co2_too()
    {
        var s = Run(acuity: Series("visualAcuity", 0.1m, 0.2m, 0.3m, 0.4m), co2: Co2(2m, 3m, 4m, 5m));

        Assert.Equal(1m, s.VisualAcuityVsCo2.R);
        Assert.Equal("visualAcuity", s.VisualAcuityVsCo2.IndicatorId);
    }

    // ---- the catalog ----------------------------------------------------------------------------------------------

    [Fact]
    public void Values_outside_every_catalog_band_are_watch_instead_of_an_error()
    {
        var iop = IndicatorsCatalog.Find("iop")!;
        var gravity = IndicatorsCatalog.Find("gravity")!;
        var co2 = IndicatorsCatalog.Find("co2")!;

        Assert.Equal(HealthStatus.Watch, IndicatorsCatalog.Evaluate(iop, 5m).Status);
        Assert.Equal(HealthStatus.Watch, IndicatorsCatalog.Evaluate(gravity, 1.5m).Status);
        Assert.Equal(HealthStatus.Watch, IndicatorsCatalog.Evaluate(co2, -1m).Status);
        Assert.Equal("Outside the usual range", IndicatorsCatalog.Evaluate(iop, 5m).Label);
    }

    [Fact]
    public void The_vision_rules_live_in_the_catalog_with_source_and_status()
    {
        using var doc = IndicatorsCatalog.Parse();
        var vision = doc.RootElement.GetProperty("vision");

        Assert.Equal("nasa-sans", vision.GetProperty("source").GetString());
        Assert.Equal("heuristic", vision.GetProperty("status").GetString());
        Assert.True(doc.RootElement.GetProperty("sources").TryGetProperty("nasa-sans", out _));
        Assert.Equal(CatalogStatus.Heuristic, VisionAnalysis.CatalogStatus);
    }

    [Fact]
    public void Gravity_above_the_catalog_range_no_longer_breaks_the_risk_engine()
    {
        var jupiterish = new EnvironmentProfile("x", 2.4m, 100m, 4, 200m);

        var risk = RiskEngine.Evaluate(new RiskInputs(Profile: jupiterish));

        Assert.Equal(HealthStatus.Watch, risk.Hazards.Single(h => h.Hazard == Hazard.GravityFields).Status);
    }

    [Fact]
    public void The_latest_eye_readings_feed_the_gravity_fields_hazard()
    {
        var vision = new[] { Reading("iop", 27m, 0), Reading("visualAcuity", 0.2m, 0) };

        var hazard = RiskEngine.Evaluate(new RiskInputs(Vision: vision)).Hazards.Single(h => h.Hazard == Hazard.GravityFields);

        Assert.Equal(HealthStatus.Critical, hazard.Status);
        Assert.Contains("Presión intraocular", hazard.Explanation);
        Assert.Equal("mmHg", hazard.Unit);
        Assert.Equal(21m, hazard.Limit);
    }
}
