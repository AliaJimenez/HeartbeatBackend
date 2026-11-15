using System.Text.Json;

namespace Heartbeat.Shared;

public sealed record IndicatorBand(string Id, HealthStatus Status, decimal? Min, decimal? Max, string Label);

public sealed record IndicatorCatalogEntry(
    string Id,
    string Name,
    string Unit,
    Hazard Hazard,
    string Source,
    CatalogStatus Status,
    IReadOnlyList<IndicatorBand> Bands,
    decimal BaselineThreshold = 2m);

public static class IndicatorsCatalog
{
    public const string ResourceName = "Heartbeat.Shared.indicators.catalog.json";

    private static readonly Lazy<string> _json = new(() =>
    {
        using var stream = OpenStream();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static readonly Lazy<string> _version = new(() =>
    {
        using var doc = JsonDocument.Parse(_json.Value);
        return doc.RootElement.GetProperty("version").GetString() ?? "unknown";
    });

    public static string Json => _json.Value;

    public static string Version => _version.Value;

    public static Stream OpenStream()
    {
        var assembly = typeof(IndicatorsCatalog).Assembly;
        var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found in {assembly.FullName}.");
        }
        return stream;
    }

    public static JsonDocument Parse() => JsonDocument.Parse(Json);

    public static IReadOnlyList<IndicatorCatalogEntry> All()
    {
        using var doc = Parse();
        var sources = doc.RootElement.GetProperty("sources");
        var entries = new List<IndicatorCatalogEntry>();
        foreach (var ind in doc.RootElement.GetProperty("indicators").EnumerateArray())
        {
            var bands = new List<IndicatorBand>();
            foreach (var b in ind.GetProperty("bands").EnumerateArray())
            {
                bands.Add(new IndicatorBand(
                    b.TryGetProperty("id", out var id) ? (id.GetString() ?? string.Empty) : string.Empty,
                    Enum.Parse<HealthStatus>(b.GetProperty("status").GetString()!, ignoreCase: true),
                    b.TryGetProperty("min", out var min) ? min.GetDecimal() : null,
                    b.TryGetProperty("max", out var max) ? max.GetDecimal() : null,
                    b.TryGetProperty("label", out var label) ? (label.GetString() ?? string.Empty) : string.Empty));
            }

            var baselineThreshold = ind.TryGetProperty("baselineThreshold", out var bt) ? bt.GetDecimal() : 2m;

            entries.Add(new IndicatorCatalogEntry(
                ind.GetProperty("id").GetString()!,
                ind.GetProperty("name").GetString()!,
                ind.GetProperty("unit").GetString()!,
                Enum.Parse<Hazard>(ind.GetProperty("hazard").GetString()!, ignoreCase: true),
                ind.GetProperty("source").GetString()!,
                Enum.Parse<CatalogStatus>(ind.GetProperty("status").GetString()!, ignoreCase: true),
                bands,
                baselineThreshold));
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("The indicators catalog is empty.");
        }

        // No gaps / overlaps between bands of the same indicator: every band covers min <= value < max.
        foreach (var entry in entries)
        {
            ValidateBands(entry.Id, entry.Bands);
        }

        // Every source id referenced by an indicator must exist in the file's sources.
        foreach (var entry in entries)
        {
            if (!sources.TryGetProperty(entry.Source, out _))
            {
                throw new InvalidOperationException($"Indicator '{entry.Id}' references an unknown source '{entry.Source}'.");
            }
        }

        return entries;
    }

    /// <summary>The bands of one indicator must not overlap, leave gaps or be empty (each covers min &lt;= value &lt; max).</summary>
    internal static void ValidateBands(string indicatorId, IReadOnlyList<IndicatorBand> bands)
    {
        var sorted = bands.OrderBy(b => b.Min ?? decimal.MinValue).ToList();
        decimal? previousMax = null;
        foreach (var band in sorted)
        {
            if (previousMax.HasValue && band.Min.HasValue && band.Min.Value != previousMax.Value)
            {
                throw new InvalidOperationException($"Indicator '{indicatorId}' has a gap between {previousMax} and {band.Min}.");
            }
            if (band.Min.HasValue && band.Max.HasValue && band.Min.Value >= band.Max.Value)
            {
                throw new InvalidOperationException($"Indicator '{indicatorId}' has an empty or inverted band.");
            }
            previousMax = band.Max;
        }
    }

    private static readonly Lazy<IReadOnlyList<IndicatorCatalogEntry>> _all = new(All);

    /// <summary>The catalog entries, parsed and validated once.</summary>
    public static IReadOnlyList<IndicatorCatalogEntry> Cached => _all.Value;

    public static IndicatorCatalogEntry? Find(string? id)
        => id is null ? null : Cached.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The band a value falls in: <c>Min &lt;= value &lt; Max</c> (an open end is unbounded). Some entries do not cover the whole
    /// line (IOP below 10 mmHg, gravity above 1.1 g, a negative CO2): a value outside every band is not normal, so it
    /// comes back as <see cref="OutsideBand"/> (watch) instead of failing.
    /// </summary>
    public static IndicatorBand Evaluate(IndicatorCatalogEntry entry, decimal value)
        => entry.Bands.FirstOrDefault(b => (b.Min is null || value >= b.Min) && (b.Max is null || value < b.Max)) ?? OutsideBand;

    public static readonly IndicatorBand OutsideBand =
        new("outside-range", HealthStatus.Watch, null, null, "Outside the usual range");

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SourceLink> _sources = new();

    public static SourceLink SourceFor(IndicatorCatalogEntry entry) => SourceFor(entry.Source);

    public static SourceLink SourceFor(string sourceId)
        => _sources.GetOrAdd(sourceId, id =>
        {
            using var doc = Parse();
            var source = doc.RootElement.GetProperty("sources").GetProperty(id);
            return new SourceLink(source.GetProperty("title").GetString()!, new Uri(source.GetProperty("url").GetString()!));
        });
}
