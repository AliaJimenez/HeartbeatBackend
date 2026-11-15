using Heartbeat.Shared;

namespace Heartbeat.Tests;

/// <summary>HB-BE-07 (#103): unit tests of the shared risk engine, one group per hazard and the global status.</summary>
public class RiskEngineTests
{
    private static readonly EnvironmentProfile Station = new("station", 0m, 408m, 4, 388m);
    private static readonly EnvironmentProfile Moon = new("lunarBase", 0.17m, 384_400m, 4, 120m);
    private static readonly EnvironmentProfile Earthlike = new("lab", 1m, 100m, 8, 600m);
    private static readonly EnvironmentProfile Mars = new("spacecraft", 0.38m, 225_000_000m, 4, 100m);

    private static RiskStatus Run(RiskInputs inputs) => RiskEngine.Evaluate(inputs);

    private static HazardStatus Of(RiskStatus risk, Hazard hazard) => risk.Hazards.Single(h => h.Hazard == hazard);

    private static CheckUpEvaluation Vitals(params ReadingInput[] inputs) => new HealthEvaluator().Evaluate(inputs);

    private static RadiationSummaryDto Radiation(decimal total, decimal spe = 0m, int entries = 3) => new(
        total, entries, [], ReferenceRanges.GetSource(RadiationDoses.SourceId), spe > 0 ? RadiationType.Spe : RadiationType.Gcr, 0.5m,
        RadiationDoses.PercentOfCareerLimit(total), spe, RadiationDoses.PercentOfSpeLimit(spe),
        RadiationDoses.WorstOf(
            RadiationDoses.StatusForCareerPercent(RadiationDoses.PercentOfCareerLimit(total)),
            RadiationDoses.StatusForSpePercent(RadiationDoses.PercentOfSpeLimit(spe))));

    private static EnvironmentReadingStatus Env(string id, decimal value)
    {
        var entry = IndicatorsCatalog.Find(id)!;
        var band = IndicatorsCatalog.Evaluate(entry, value);
        return new EnvironmentReadingStatus(
            Guid.NewGuid(), DateTimeOffset.UtcNow, null, entry.Id, entry.Name, value, null, entry.Unit,
            band.Status, string.IsNullOrWhiteSpace(band.Label) ? RiskEngine.Word(band.Status) : band.Label,
            entry.Status, IndicatorsCatalog.SourceFor(entry), null);
    }

    // ---- global ---------------------------------------------------------------------------------------------------

    [Fact]
    public void No_data_gives_five_normal_hazards_in_order_and_a_normal_global()
    {
        var risk = Run(new RiskInputs());

        Assert.Equal(HealthStatus.Normal, risk.Status);
        Assert.Equal("All five hazards are normal.", risk.Explanation);
        Assert.Equal(Enum.GetValues<Hazard>(), risk.Hazards.Select(h => h.Hazard));
        Assert.All(risk.Hazards, h =>
        {
            Assert.Equal(HealthStatus.Normal, h.Status);
            Assert.False(string.IsNullOrWhiteSpace(h.Explanation));
        });
    }

    [Fact]
    public void Healthy_data_everywhere_stays_green_with_sources_and_numbers()
    {
        var risk = Run(new RiskInputs(
            Vitals(new ReadingInput(IndicatorType.BloodPressure, 118, 76), new ReadingInput(IndicatorType.HeartRate, 66),
                new ReadingInput(IndicatorType.Sleep, 7.5m), new ReadingInput(IndicatorType.Mood, 4)),
            Radiation(120m), [Env("co2", 2m), Env("o2", 170m)], Earthlike, IsolationConfinement.Evaluate(Earthlike)));

        Assert.Equal(HealthStatus.Normal, risk.Status);
        Assert.All(risk.Hazards, h => Assert.Equal(HealthStatus.Normal, h.Status));
        Assert.NotNull(Of(risk, Hazard.SpaceRadiation).Source);
    }

    [Fact]
    public void Global_is_the_worst_hazard_and_the_explanation_leads_with_it_and_names_the_others()
    {
        var risk = Run(new RiskInputs(Radiation: Radiation(350m), Environment: [Env("co2", 6m)]));

        Assert.Equal(HealthStatus.Critical, risk.Status);
        Assert.StartsWith("Hostile closed environment is critical:", risk.Explanation);
        Assert.Contains("space radiation (watch)", risk.Explanation);
        Assert.Equal(
            risk.Hazards.Max(h => h.Status),
            risk.Status);
    }

    [Fact]
    public void Every_non_green_hazard_has_an_explanation_and_a_source()
    {
        var risk = Run(new RiskInputs(
            Vitals(new ReadingInput(IndicatorType.BloodPressure, 190, 125), new ReadingInput(IndicatorType.BloodSugar, 300),
                new ReadingInput(IndicatorType.Sleep, 3m)),
            Radiation(700m), [Env("noise", 80m)], Mars, IsolationConfinement.Evaluate(Mars, 24m, 24m)));

        var flagged = risk.Hazards.Where(h => h.Status != HealthStatus.Normal).ToList();
        Assert.Equal(5, flagged.Count);
        Assert.All(flagged, h =>
        {
            Assert.False(string.IsNullOrWhiteSpace(h.Explanation));
            Assert.NotNull(h.Source);
        });
    }

    [Fact]
    public void Aliases_and_the_deviations_and_indices_pass_through()
    {
        var indices = IsolationConfinement.Evaluate(Earthlike);
        var deviations = new[] { new PersonalDeviation("heartRate", 90m, 60m, 5m, 6m) };

        var risk = Run(new RiskInputs(Isolation: indices, Deviations: deviations));

        Assert.Same(indices, risk.Indices);
        Assert.Single(risk.PersonalDeviations!);
        Assert.Equal(risk.Status, risk.GlobalStatus);
    }

    // ---- space radiation ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, HealthStatus.Normal)]
    [InlineData(299, HealthStatus.Normal)]
    [InlineData(300, HealthStatus.Watch)]
    [InlineData(599, HealthStatus.Watch)]
    [InlineData(600, HealthStatus.Critical)]
    public void Radiation_follows_the_career_limit(decimal total, HealthStatus expected)
    {
        var hazard = Of(Run(new RiskInputs(Radiation: Radiation(total))), Hazard.SpaceRadiation);

        Assert.Equal(expected, hazard.Status);
        Assert.Equal(total, hazard.Value);
        Assert.Equal(RadiationDoses.CareerLimitMsv, hazard.Limit);
        Assert.Equal("mSv", hazard.Unit);
        Assert.Equal(RadiationDoses.SourceId, "csa-radiation");
        Assert.NotNull(hazard.Source);
    }

    [Fact]
    public void Radiation_explanation_gives_the_percent_of_the_limit_in_plain_words()
    {
        var hazard = Of(Run(new RiskInputs(Radiation: Radiation(450m))), Hazard.SpaceRadiation);

        Assert.Contains("75% of the 600 mSv career limit", hazard.Explanation);
        Assert.Contains("not a diagnosis", hazard.Explanation);
    }

    [Fact]
    public void A_big_solar_event_alone_turns_radiation_red_even_with_a_low_total()
    {
        var hazard = Of(Run(new RiskInputs(Radiation: Radiation(260m, spe: 260m))), Hazard.SpaceRadiation);

        Assert.Equal(HealthStatus.Critical, hazard.Status);
        Assert.Contains("solar particle event", hazard.Explanation);
        Assert.Contains("250 mSv per-event limit", hazard.Explanation);
    }

    [Fact]
    public void Radiation_without_doses_says_so()
    {
        var hazard = Of(Run(new RiskInputs(Radiation: Radiation(0m, entries: 0))), Hazard.SpaceRadiation);

        Assert.Equal(HealthStatus.Normal, hazard.Status);
        Assert.Contains("No radiation doses", hazard.Explanation);
    }

    // ---- hostile closed environment -------------------------------------------------------------------------------

    [Theory]
    [InlineData("co2", 2.5, HealthStatus.Normal)]
    [InlineData("co2", 4, HealthStatus.Watch)]
    [InlineData("co2", 5, HealthStatus.Critical)]
    [InlineData("o2", 135, HealthStatus.Critical)]
    [InlineData("humidity", 35, HealthStatus.Watch)]
    public void Habitat_readings_set_the_environment_hazard(string indicator, double value, HealthStatus expected)
    {
        var hazard = Of(Run(new RiskInputs(Environment: [Env(indicator, (decimal)value)])), Hazard.HostileClosedEnvironment);

        Assert.Equal(expected, hazard.Status);
    }

    [Fact]
    public void Environment_hazard_reports_the_worst_reading_with_the_edge_of_its_normal_band()
    {
        var hazard = Of(Run(new RiskInputs(Environment: [Env("co2", 6.2m), Env("noise", 60m), Env("light", 400m)])), Hazard.HostileClosedEnvironment);

        Assert.Equal(HealthStatus.Critical, hazard.Status);
        Assert.Equal(6.2m, hazard.Value);
        Assert.Equal(3m, hazard.Limit);                    // CO2 is normal below 3 mmHg
        Assert.Equal("mmHg", hazard.Unit);
        Assert.Contains("simulated", hazard.Explanation);
        Assert.Contains("6.2", hazard.Explanation);
        Assert.NotNull(hazard.Source);
    }

    [Fact]
    public void A_reading_below_the_normal_band_reports_the_lower_edge()
    {
        var hazard = Of(Run(new RiskInputs(Environment: [Env("o2", 130m)])), Hazard.HostileClosedEnvironment);

        Assert.Equal(160m, hazard.Limit);
    }

    [Fact]
    public void Blood_sugar_and_body_temperature_belong_to_the_closed_environment_hazard()
    {
        var risk = Run(new RiskInputs(Vitals(new ReadingInput(IndicatorType.BloodSugar, 300), new ReadingInput(IndicatorType.BodyTemperature, 36.6m))));

        var hazard = Of(risk, Hazard.HostileClosedEnvironment);
        Assert.Equal(HealthStatus.Critical, hazard.Status);
        Assert.Contains("Blood sugar", hazard.Explanation);
        Assert.DoesNotContain("Body temperature", hazard.Explanation);   // normal findings are not listed when something is flagged
    }

    // ---- gravity fields -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(120, 79, HealthStatus.Normal)]
    [InlineData(150, 85, HealthStatus.Watch)]
    [InlineData(190, 125, HealthStatus.Critical)]
    public void Blood_pressure_sets_the_gravity_hazard(decimal systolic, decimal diastolic, HealthStatus expected)
    {
        var risk = Run(new RiskInputs(Vitals(new ReadingInput(IndicatorType.BloodPressure, systolic, diastolic))));

        Assert.Equal(expected, Of(risk, Hazard.GravityFields).Status);
    }

    [Fact]
    public void Heart_rate_sets_the_gravity_hazard_and_carries_the_first_piece_of_advice()
    {
        var hazard = Of(Run(new RiskInputs(Vitals(new ReadingInput(IndicatorType.HeartRate, 130)))), Hazard.GravityFields);

        Assert.Equal(HealthStatus.Critical, hazard.Status);
        Assert.Contains("Heart rate is very fast", hazard.Explanation);
        Assert.Contains("Stop all activity and rest", hazard.Explanation);
        Assert.NotNull(hazard.Source);
    }

    [Fact]
    public void Low_gravity_of_the_mission_warns_but_never_turns_red_by_itself()
    {
        var station = Of(Run(new RiskInputs(Profile: Station)), Hazard.GravityFields);
        var moon = Of(Run(new RiskInputs(Profile: Moon)), Hazard.GravityFields);
        var earth = Of(Run(new RiskInputs(Profile: Earthlike)), Hazard.GravityFields);

        Assert.Equal(HealthStatus.Watch, station.Status);   // 0 g is critical in the catalog; planned conditions are capped
        Assert.Equal(HealthStatus.Watch, moon.Status);
        Assert.Equal(HealthStatus.Normal, earth.Status);
        Assert.Contains("planned condition", station.Explanation);
    }

    [Fact]
    public void A_critical_vital_still_beats_the_capped_gravity_of_the_mission()
    {
        var hazard = Of(Run(new RiskInputs(Vitals(new ReadingInput(IndicatorType.BloodPressure, 190, 125)), Profile: Station)), Hazard.GravityFields);

        Assert.Equal(HealthStatus.Critical, hazard.Status);
    }

    // ---- distance from earth --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(408, HealthStatus.Normal)]               // station: about a hundredth of a second of delay
    [InlineData(384_400, HealthStatus.Normal)]           // the Moon: 1.3 s
    [InlineData(40_000_000, HealthStatus.Watch)]         // a few minutes: watch
    [InlineData(225_000_000, HealthStatus.Watch)]        // Mars at 12.5 min is critical in the catalog, capped to watch
    public void Distance_uses_the_communication_delay_bands_of_the_catalog(decimal km, HealthStatus expected)
    {
        var profile = new EnvironmentProfile("x", 1m, km, 4, 200m);

        var hazard = Of(Run(new RiskInputs(Profile: profile)), Hazard.DistanceFromEarth);

        Assert.Equal(expected, hazard.Status);
    }

    [Fact]
    public void Distance_explanation_gives_the_delay_in_minutes_and_a_source()
    {
        var hazard = Of(Run(new RiskInputs(Profile: Mars)), Hazard.DistanceFromEarth);

        Assert.Contains("12.5 min", hazard.Explanation);
        Assert.Equal("min", hazard.Unit);
        Assert.Equal(2m, hazard.Limit);
        Assert.NotNull(hazard.Source);
    }

    // ---- isolation and confinement --------------------------------------------------------------------------------

    [Theory]
    [InlineData(8, HealthStatus.Normal)]
    [InlineData(5, HealthStatus.Watch)]
    [InlineData(3, HealthStatus.Critical)]
    public void Sleep_sets_the_isolation_hazard(decimal hours, HealthStatus expected)
    {
        var hazard = Of(Run(new RiskInputs(Vitals(new ReadingInput(IndicatorType.Sleep, hours)))), Hazard.IsolationAndConfinement);

        Assert.Equal(expected, hazard.Status);
    }

    [Theory]
    [InlineData(4, HealthStatus.Normal)]
    [InlineData(2.5, HealthStatus.Watch)]
    [InlineData(1, HealthStatus.Critical)]
    public void Mood_sets_the_isolation_hazard(double mood, HealthStatus expected)
    {
        var hazard = Of(Run(new RiskInputs(Vitals(new ReadingInput(IndicatorType.Mood, (decimal)mood)))), Hazard.IsolationAndConfinement);

        Assert.Equal(expected, hazard.Status);
    }

    [Fact]
    public void The_isolation_indices_feed_the_isolation_hazard()
    {
        var indices = IsolationConfinement.Evaluate(Mars, 24m, 24m);
        var hazard = Of(Run(new RiskInputs(Isolation: indices)), Hazard.IsolationAndConfinement);

        Assert.Equal(indices.Status, hazard.Status);
        Assert.Contains("Simulated indices", hazard.Explanation);
    }

    [Fact]
    public void Isolation_combines_the_worse_of_vitals_and_indices()
    {
        var indices = IsolationConfinement.Evaluate(Earthlike);   // low
        var hazard = Of(Run(new RiskInputs(Vitals(new ReadingInput(IndicatorType.Mood, 1)), Isolation: indices)), Hazard.IsolationAndConfinement);

        Assert.Equal(HealthStatus.Critical, hazard.Status);
        Assert.Contains("Mood is very bad", hazard.Explanation);
    }

    // ---- mapping --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(IndicatorType.BloodPressure, Hazard.GravityFields)]
    [InlineData(IndicatorType.HeartRate, Hazard.GravityFields)]
    [InlineData(IndicatorType.BloodSugar, Hazard.HostileClosedEnvironment)]
    [InlineData(IndicatorType.BodyTemperature, Hazard.HostileClosedEnvironment)]
    [InlineData(IndicatorType.Sleep, Hazard.IsolationAndConfinement)]
    [InlineData(IndicatorType.Mood, Hazard.IsolationAndConfinement)]
    public void Vital_signs_are_grouped_by_the_hazard_declared_in_the_reference_ranges(IndicatorType type, Hazard expected)
        => Assert.Equal(expected, RiskEngine.HazardOf(type));

    [Fact]
    public void The_engine_is_deterministic()
    {
        var inputs = new RiskInputs(
            Vitals(new ReadingInput(IndicatorType.HeartRate, 130)), Radiation(350m), [Env("co2", 4m)], Mars, IsolationConfinement.Evaluate(Mars));

        Assert.Equal(Run(inputs).Explanation, Run(inputs).Explanation);
        Assert.Equal(Run(inputs).Hazards.Select(h => (h.Hazard, h.Status)), Run(inputs).Hazards.Select(h => (h.Hazard, h.Status)));
    }
}
