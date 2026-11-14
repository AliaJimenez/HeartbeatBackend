using System.Text.Json;
using Heartbeat.Shared;

namespace Heartbeat.Api.Vitals;

/// <summary>
/// Read-only view of the embedded reference-ranges.json for the server: turns the band IDs stored on
/// each reading back into labels, guidance, escalation and sources (CONTRACTS.md §2).
/// </summary>
public sealed class RangesCatalog
{
    private sealed record Band(string Id, HealthStatus Status, string Label, IReadOnlyList<string> Guidance);

    private sealed record IndicatorInfo(
        string Unit,
        IReadOnlyList<SourceLink> Sources,
        Dictionary<string, Band> Bands,
        IReadOnlyList<SeriesBandDto> PrimaryBands);

    private readonly Dictionary<IndicatorType, IndicatorInfo> _indicators = new();
    private readonly Dictionary<HealthStatus, Escalation> _escalations = new();

    public string Version { get; }

    public RangesCatalog() : this(ReferenceRanges.Json) { }

    public RangesCatalog(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Version = root.GetProperty("version").GetString() ?? "unknown";

        var sources = root.GetProperty("sources").EnumerateObject().ToDictionary(
            p => p.Name,
            p => new SourceLink(p.Value.GetProperty("title").GetString()!, new Uri(p.Value.GetProperty("url").GetString()!)));

        foreach (var (status, key) in new[] { (HealthStatus.Watch, "watch"), (HealthStatus.Critical, "critical") })
        {
            var e = root.GetProperty("escalation").GetProperty(key);
            var contact = string.Equals(e.GetProperty("contact").GetString(), "self", StringComparison.OrdinalIgnoreCase)
                ? EscalationContact.Self
                : EscalationContact.CrewMedicalOfficer;
            _escalations[status] = new Escalation(contact, e.GetProperty("message").GetString()!);
        }

        foreach (var ind in root.GetProperty("indicators").EnumerateArray())
        {
            var type = Enum.Parse<IndicatorType>(ind.GetProperty("id").GetString()!, ignoreCase: true);
            var bands = new Dictionary<string, Band>();
            var primaryBands = new List<SeriesBandDto>();

            // Single-value indicators have "bands"; blood pressure has "components" (systolic/diastolic).
            // The first group is the primary value (the one charts shade against).
            var groups = ind.TryGetProperty("bands", out var flat)
                ? [flat]
                : ind.GetProperty("components").EnumerateObject().Select(p => p.Value).ToList();

            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                foreach (var b in groups[groupIndex].EnumerateArray())
                {
                    if (groupIndex == 0)
                    {
                        primaryBands.Add(new SeriesBandDto(
                            Enum.Parse<HealthStatus>(b.GetProperty("status").GetString()!, ignoreCase: true),
                            b.TryGetProperty("min", out var min) ? min.GetDecimal() : null,
                            b.TryGetProperty("max", out var max) ? max.GetDecimal() : null));
                    }

                    var id = b.GetProperty("id").GetString()!;
                    bands[id] = new Band(
                        id,
                        Enum.Parse<HealthStatus>(b.GetProperty("status").GetString()!, ignoreCase: true),
                        b.GetProperty("label").GetString()!,
                        b.GetProperty("guidance").EnumerateArray().Select(g => g.GetString()!).ToList());
                }
            }

            var links = ind.GetProperty("sources").EnumerateArray()
                .Select(s => s.GetString()!)
                .Where(sources.ContainsKey)
                .Select(s => sources[s])
                .ToList();

            _indicators[type] = new IndicatorInfo(ind.GetProperty("unit").GetString()!, links, bands, primaryBands);
        }
    }

    public string UnitFor(IndicatorType indicator) => _indicators.TryGetValue(indicator, out var i) ? i.Unit : string.Empty;

    /// <summary>Bands of the indicator's primary value (systolic for blood pressure), in ascending order, for chart shading.</summary>
    public IReadOnlyList<SeriesBandDto> BandsFor(IndicatorType indicator)
        => _indicators.TryGetValue(indicator, out var i) ? i.PrimaryBands : [];

    /// <summary>Label of the worst band among the reading's band IDs (the first one when tied).</summary>
    public string LabelFor(IndicatorType indicator, IEnumerable<string> bandIds)
        => WorstBands(indicator, bandIds).FirstOrDefault()?.Label ?? string.Empty;

    public ReadingEvaluation Evaluate(IndicatorType indicator, HealthStatus status, IReadOnlyList<string> bandIds)
    {
        var worst = WorstBands(indicator, bandIds);
        var guidance = worst.SelectMany(b => b.Guidance).Distinct().ToList();
        _escalations.TryGetValue(status, out var escalation);
        var sources = _indicators.TryGetValue(indicator, out var info) ? info.Sources : [];

        return new ReadingEvaluation(
            indicator,
            status,
            bandIds,
            worst.FirstOrDefault()?.Label ?? string.Empty,
            guidance,
            escalation,
            sources);
    }

    // All bands that share the highest status; unknown IDs (e.g. from an older ranges file) are skipped.
    private List<Band> WorstBands(IndicatorType indicator, IEnumerable<string> bandIds)
    {
        if (!_indicators.TryGetValue(indicator, out var info))
        {
            return [];
        }

        var known = bandIds.Where(info.Bands.ContainsKey).Select(id => info.Bands[id]).ToList();
        if (known.Count == 0)
        {
            return [];
        }

        var max = known.Max(b => b.Status);
        return known.Where(b => b.Status == max).ToList();
    }
}
