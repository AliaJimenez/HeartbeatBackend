using System.Text.Json;

namespace Heartbeat.Shared;

/// <summary>
/// Personal baseline for an astronaut and an indicator, built from the first N readings of that
/// astronaut. Used to detect deviations ("fuera de tu normal") instead of only population thresholds.
/// All data is simulated and the result is heuristic.
/// </summary>
public sealed record PersonalBaseline(
    string IndicatorId,
    decimal Mean,
    decimal StdDev,
    int Count,
    decimal Threshold)
{
    public bool HasEnoughData => Count >= 3;

    public decimal ZScore(decimal value) => StdDev == 0m ? 0m : (value - Mean) / StdDev;

    public bool IsDeviation(decimal value) =>
        HasEnoughData && StdDev > 0m && Math.Abs(ZScore(value)) >= Threshold;

    public static PersonalBaseline Compute(
        string indicatorId,
        IEnumerable<decimal> values,
        int baselineDays = 7,
        decimal threshold = 2m)
    {
        var baseline = values.Take(baselineDays).ToList();
        if (baseline.Count == 0)
        {
            return new PersonalBaseline(indicatorId, 0m, 0m, 0, threshold);
        }

        var mean = baseline.Average();
        var variance = baseline.Count <= 1
            ? 0m
            : baseline.Sum(v => (v - mean) * (v - mean)) / (baseline.Count - 1);
        var stdDev = (decimal)Math.Sqrt((double)variance);

        return new PersonalBaseline(indicatorId, Math.Round(mean, 4, MidpointRounding.AwayFromZero), Math.Round(stdDev, 4, MidpointRounding.AwayFromZero), baseline.Count, threshold);
    }

    public static decimal Ewma(IEnumerable<decimal> values, decimal alpha = 0.3m)
    {
        decimal? result = null;
        foreach (var v in values)
        {
            result = result is null ? v : alpha * v + (1m - alpha) * result.Value;
        }
        return result ?? 0m;
    }
}

/// <summary>A single indicator that is outside the astronaut's personal baseline.</summary>
public sealed record PersonalDeviation(
    string IndicatorId,
    decimal Value,
    decimal Mean,
    decimal StdDev,
    decimal ZScore);
