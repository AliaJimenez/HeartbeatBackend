namespace Heartbeat.Shared;

/// <summary>
/// HB-BE-05: trend of IOP and visual acuity and their simple correlation with CO2 (SANS). Pure and deterministic, in
/// Heartbeat.Shared like the other engines, so the desktop can show the same numbers offline. Bands, sources and the
/// rules that decide "stable" and "weak / strong" correlation come from <c>indicators.catalog.json</c>.
/// </summary>
public static class VisionAnalysis
{
    public const string IopId = "iop";
    public const string VisualAcuityId = "visualAcuity";
    public const string Co2Id = "co2";

    /// <summary>The vision indicators that enter through sync alongside the habitat readings.</summary>
    public static bool IsVisionIndicator(string? id)
        => string.Equals(id, IopId, StringComparison.OrdinalIgnoreCase) || string.Equals(id, VisualAcuityId, StringComparison.OrdinalIgnoreCase);

    private sealed record Rules(
        IReadOnlyDictionary<string, decimal> StableDelta, int MinPairs, decimal WeakBelow, decimal StrongFrom, CatalogStatus Status);

    private static readonly Lazy<Rules> _rules = new(() =>
    {
        using var doc = IndicatorsCatalog.Parse();
        var vision = doc.RootElement.GetProperty("vision");
        var stable = vision.GetProperty("trend").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetProperty("stableDelta").GetDecimal(), StringComparer.OrdinalIgnoreCase);
        var c = vision.GetProperty("correlation");
        return new Rules(
            stable,
            c.GetProperty("minPairs").GetInt32(),
            c.GetProperty("weakBelow").GetDecimal(),
            c.GetProperty("strongFrom").GetDecimal(),
            Enum.Parse<CatalogStatus>(vision.GetProperty("status").GetString()!, ignoreCase: true));
    });

    public static CatalogStatus CatalogStatus => _rules.Value.Status;

    /// <summary>Builds the whole SANS summary from the readings of one period (any order; other indicators are ignored).</summary>
    public static VisionSummaryDto Analyze(
        IEnumerable<EnvironmentReadingStatus> iop,
        IEnumerable<EnvironmentReadingStatus> visualAcuity,
        IEnumerable<VisionPoint> co2,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var iopTrend = Trend(IopId, iop);
        var acuityTrend = Trend(VisualAcuityId, visualAcuity);
        var co2Points = co2.ToList();

        var status = (HealthStatus)Math.Max((int)iopTrend.Status, (int)acuityTrend.Status);
        return new VisionSummaryDto(
            status,
            iopTrend,
            acuityTrend,
            CorrelateWithCo2(iopTrend, co2Points),
            CorrelateWithCo2(acuityTrend, co2Points),
            CatalogStatus,
            IndicatorsCatalog.SourceFor(IndicatorsCatalog.Find(IopId)!),
            from,
            to);
    }

    public static IndicatorTrend Trend(string indicatorId, IEnumerable<EnvironmentReadingStatus> readings)
    {
        var entry = IndicatorsCatalog.Find(indicatorId)
            ?? throw new InvalidOperationException($"The indicators catalog has no '{indicatorId}' entry.");
        var ordered = readings
            .Where(r => string.Equals(r.IndicatorId, entry.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.RecordedAt)
            .ToList();
        var source = IndicatorsCatalog.SourceFor(entry);

        if (ordered.Count == 0)
        {
            return new IndicatorTrend(entry.Id, entry.Name, entry.Unit, 0, TrendDirection.Unknown, null, null, null, null,
                HealthStatus.Normal, $"No {entry.Name} readings in this period (simulated data).", entry.Status, source, []);
        }

        var latest = ordered[^1];
        var (slope, change) = Fit(ordered.Select(r => (r.RecordedAt, r.Value)).ToList());
        var direction = TrendDirection.Unknown;
        if (ordered.Count >= 2 && slope is not null && change is not null)
        {
            var stableDelta = _rules.Value.StableDelta.GetValueOrDefault(entry.Id, 0m);
            direction = Math.Abs(change.Value) < stableDelta ? TrendDirection.Stable
                : slope > 0 ? TrendDirection.Rising : TrendDirection.Falling;
        }

        return new IndicatorTrend(
            entry.Id, entry.Name, entry.Unit, ordered.Count, direction, change, slope, latest.Value, latest.RecordedAt, latest.Status,
            TrendText(entry, ordered, direction, change, latest), entry.Status, source, ordered);
    }

    private static string TrendText(
        IndicatorCatalogEntry entry, IReadOnlyList<EnvironmentReadingStatus> ordered, TrendDirection direction, decimal? change, EnvironmentReadingStatus latest)
    {
        var now = $"The latest reading is {latest.Value:0.##} {entry.Unit} ({RiskEngine.Word(latest.Status)})";
        if (ordered.Count < 2)
        {
            return $"{now}. One reading is not enough for a trend. Simulated data, not a diagnosis.";
        }

        var days = Math.Max(1, (int)Math.Round((ordered[^1].RecordedAt - ordered[0].RecordedAt).TotalDays, MidpointRounding.AwayFromZero));
        var movement = direction switch
        {
            TrendDirection.Rising => $"{entry.Name} is rising: about {Math.Abs(change!.Value):0.##} {entry.Unit} higher over {days} day(s)",
            TrendDirection.Falling => $"{entry.Name} is falling: about {Math.Abs(change!.Value):0.##} {entry.Unit} lower over {days} day(s)",
            _ => $"{entry.Name} is stable over {days} day(s)",
        };
        return $"{movement}. {now}. Simulated data, not a diagnosis.";
    }

    /// <summary>Least-squares line of value against time: slope per day and the change it shows from the first to the last reading.</summary>
    private static (decimal? Slope, decimal? Change) Fit(IReadOnlyList<(DateTimeOffset At, decimal Value)> points)
    {
        if (points.Count < 2)
        {
            return (null, null);
        }

        var t0 = points[0].At;
        var xs = points.Select(p => (p.At - t0).TotalDays).ToList();
        var ys = points.Select(p => (double)p.Value).ToList();
        var mx = xs.Average();
        var my = ys.Average();
        var sxx = xs.Sum(x => (x - mx) * (x - mx));
        if (sxx == 0)
        {
            return (0m, 0m);
        }

        var slope = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum() / sxx;
        var change = slope * (xs[^1] - xs[0]);
        return (Math.Round((decimal)slope, 4, MidpointRounding.AwayFromZero), Math.Round((decimal)change, 4, MidpointRounding.AwayFromZero));
    }

    /// <summary>Pearson r between the daily means (UTC days) of the indicator and of CO2, over the days that have both.</summary>
    public static Co2Correlation CorrelateWithCo2(IndicatorTrend indicator, IReadOnlyList<VisionPoint> co2)
    {
        var rules = _rules.Value;
        var own = DailyMeans(indicator.Readings.Select(r => new VisionPoint(r.RecordedAt, r.Value)));
        var other = DailyMeans(co2);
        var days = own.Keys.Intersect(other.Keys).OrderBy(d => d).ToList();
        var name = indicator.Name;

        if (days.Count < rules.MinPairs)
        {
            return new Co2Correlation(indicator.IndicatorId, null, days.Count, CorrelationStrength.Unknown,
                $"Not enough days with both {name} and CO2 readings yet ({days.Count} of the {rules.MinPairs} needed).");
        }

        var xs = days.Select(d => (double)own[d]).ToList();
        var ys = days.Select(d => (double)other[d]).ToList();
        var mx = xs.Average();
        var my = ys.Average();
        var sxx = xs.Sum(x => (x - mx) * (x - mx));
        var syy = ys.Sum(y => (y - my) * (y - my));
        if (sxx == 0 || syy == 0)
        {
            return new Co2Correlation(indicator.IndicatorId, null, days.Count, CorrelationStrength.Unknown,
                $"{name} or CO2 did not vary over these {days.Count} days, so there is nothing to compare.");
        }

        var r = Math.Round((decimal)(xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum() / Math.Sqrt(sxx * syy)), 3, MidpointRounding.AwayFromZero);
        var abs = Math.Abs(r);
        var strength = abs < rules.WeakBelow ? CorrelationStrength.Weak : abs < rules.StrongFrom ? CorrelationStrength.Moderate : CorrelationStrength.Strong;
        var direction = r >= 0 ? "moved together with" : "moved opposite to";

        var text = strength == CorrelationStrength.Weak
            ? $"{name} and CO2 show little relation over {days.Count} days (r = {r:0.##})."
            : $"{name} {direction} CO2 over {days.Count} days (r = {r:0.##}, {strength.ToString().ToLowerInvariant()}).";
        return new Co2Correlation(indicator.IndicatorId, r, days.Count, strength, text + " A link in simulated data does not show a cause.");
    }

    private static Dictionary<DateOnly, decimal> DailyMeans(IEnumerable<VisionPoint> points)
        => points
            .GroupBy(p => DateOnly.FromDateTime(p.RecordedAt.UtcDateTime))
            .ToDictionary(g => g.Key, g => g.Average(p => p.Value));
}
