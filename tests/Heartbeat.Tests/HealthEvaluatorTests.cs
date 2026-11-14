using Heartbeat.Shared;

namespace Heartbeat.Tests;

public class HealthEvaluatorTests
{
    private readonly HealthEvaluator _eval = new();

    [Fact]
    public void RangesVersion_NotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(_eval.RangesVersion));
    }

    [Fact]
    public void AllNormal_ReadingsNormal()
    {
        var result = _eval.Evaluate(new[]
        {
            new ReadingInput(IndicatorType.BloodPressure, 110m, 70m),
            new ReadingInput(IndicatorType.HeartRate, 70m),
            new ReadingInput(IndicatorType.BloodSugar, 100m),
            new ReadingInput(IndicatorType.BodyTemperature, 36.5m),
            new ReadingInput(IndicatorType.Sleep, 7m),
            new ReadingInput(IndicatorType.Mood, 4m),
        });
        Assert.Equal(HealthStatus.Normal, result.OverallStatus);
        Assert.All(result.Readings, r => Assert.Equal(HealthStatus.Normal, r.Status));
    }

    [Fact]
    public void HighBloodPressure_IsCritical()
    {
        var r = _eval.Evaluate(new ReadingInput(IndicatorType.BloodPressure, 190m, 70m));
        Assert.Equal(HealthStatus.Critical, r.Status);
        Assert.Contains("bp-sys-critical-high", r.BandIds);
        Assert.NotNull(r.Escalation);
        Assert.Equal(EscalationContact.CrewMedicalOfficer, r.Escalation!.Contact);
    }

    [Fact]
    public void Critical_Reading_HasGuidance()
    {
        var r = _eval.Evaluate(new ReadingInput(IndicatorType.HeartRate, 130m));
        Assert.Equal(HealthStatus.Critical, r.Status);
        Assert.NotEmpty(r.Guidance);
    }

    [Fact]
    public void CheckUp_OverallIsWorst()
    {
        var result = _eval.Evaluate(new[]
        {
            new ReadingInput(IndicatorType.HeartRate, 70m),
            new ReadingInput(IndicatorType.Mood, 1m),
        });
        Assert.Equal(HealthStatus.Critical, result.OverallStatus);
    }

    [Fact]
    public void Validate_OutOfRange_ReturnsErrors()
    {
        var errors = _eval.Validate(new ReadingInput(IndicatorType.BloodPressure, 300m, 70m));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_InRange_ReturnsEmpty()
    {
        var errors = _eval.Validate(new ReadingInput(IndicatorType.BloodPressure, 120m, 70m));
        Assert.Empty(errors);
    }
}