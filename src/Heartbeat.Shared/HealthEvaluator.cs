using System.Text.Json;

namespace Heartbeat.Shared;

public sealed class HealthEvaluator : IHealthEvaluator
{
    private readonly JsonDocument _document;
    private readonly Dictionary<string, JsonElement> _indicators;

    public HealthEvaluator() : this(ReferenceRanges.Json) { }

    public HealthEvaluator(string json)
    {
        _document = JsonDocument.Parse(json);
        _indicators = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var indicator in _document.RootElement.GetProperty("indicators").EnumerateArray())
        {
            _indicators[indicator.GetProperty("id").GetString()!] = indicator;
        }
    }

    public string RangesVersion => _document.RootElement.GetProperty("version").GetString() ?? "unknown";

    public ReadingEvaluation Evaluate(ReadingInput input)
    {
        var indicatorId = ToIndicatorId(input.Indicator);
        if (!_indicators.TryGetValue(indicatorId, out var indicator))
        {
            throw new ArgumentException($"Unknown indicator '{indicatorId}'.", nameof(input));
        }

        var evaluation = indicator.GetProperty("evaluation").GetString();
        if (evaluation == "worstOfComponents")
        {
            return EvaluateComposite(input, indicator);
        }

        var bands = indicator.GetProperty("bands");
        var band = FindBand(bands, input.Value)
            ?? throw new InvalidOperationException($"No band matched value {input.Value} for {indicatorId}.");

        return BuildReadingEvaluation(input.Indicator, band, indicator, single: true);
    }

    public CheckUpEvaluation Evaluate(IEnumerable<ReadingInput> inputs)
    {
        var list = inputs.ToList();
        var readings = list.Select(Evaluate).ToList();
        var worst = readings.Count == 0
            ? HealthStatus.Normal
            : readings.Max(r => r.Status);

        return new CheckUpEvaluation(worst, readings, RangesVersion);
    }

    public IReadOnlyList<string> Validate(ReadingInput input)
    {
        var errors = new List<string>();
        var indicatorId = ToIndicatorId(input.Indicator);
        if (!_indicators.TryGetValue(indicatorId, out var indicator))
        {
            errors.Add($"Unknown indicator '{indicatorId}'.");
            return errors;
        }

        if (indicator.TryGetProperty("validRange", out var validRange))
        {
            if (validRange.TryGetProperty("value", out var valueRange))
            {
                ValidateRange(valueRange, input.Value, "Value", errors);
            }

            if (input.SecondaryValue.HasValue && validRange.TryGetProperty("secondaryValue", out var secondaryRange))
            {
                ValidateRange(secondaryRange, input.SecondaryValue.Value, "SecondaryValue", errors);
            }
        }

        // Blood pressure is a pair: without the diastolic value only half of the reading would be evaluated.
        if (!input.SecondaryValue.HasValue
            && indicator.TryGetProperty("valueKind", out var kind)
            && kind.GetString() == "pair")
        {
            errors.Add("SecondaryValue is required for this indicator.");
        }

        return errors;
    }

    private ReadingEvaluation EvaluateComposite(ReadingInput input, JsonElement indicator)
    {
        var components = indicator.GetProperty("components");
        var valueBand = FindBand(components.GetProperty("value"), input.Value)
            ?? throw new InvalidOperationException($"No band matched value {input.Value}.");
        JsonElement? secondaryBand = input.SecondaryValue.HasValue
            ? (FindBand(components.GetProperty("secondaryValue"), input.SecondaryValue.Value)
                ?? throw new InvalidOperationException($"No band matched secondary value {input.SecondaryValue.Value}."))
            : null;

        var status = valueBand.GetProperty("status").GetString() == "critical" || secondaryBand?.GetProperty("status").GetString() == "critical"
            ? HealthStatus.Critical
            : (valueBand.GetProperty("status").GetString() == "watch" || secondaryBand?.GetProperty("status").GetString() == "watch"
                ? HealthStatus.Watch
                : HealthStatus.Normal);

        // Recompute status as max of parsed statuses for correctness.
        var statuses = new List<HealthStatus> { ParseStatus(valueBand.GetProperty("status").GetString()!) };
        if (secondaryBand is not null)
        {
            statuses.Add(ParseStatus(secondaryBand.Value.GetProperty("status").GetString()!));
        }

        status = statuses.Max();

        var bandIds = new List<string> { valueBand.GetProperty("id").GetString()! };
        if (secondaryBand is not null)
        {
            bandIds.Add(secondaryBand.Value.GetProperty("id").GetString()!);
        }

        var label = status == HealthStatus.Critical
            ? (valueBand.GetProperty("status").GetString() == "critical" ? valueBand.GetProperty("label").GetString()! : secondaryBand!.Value.GetProperty("label").GetString()!)
            : (status == HealthStatus.Watch
                ? (valueBand.GetProperty("status").GetString() == "watch" ? valueBand.GetProperty("label").GetString()! : (secondaryBand.HasValue && secondaryBand.Value.GetProperty("status").GetString() == "watch" ? secondaryBand.Value.GetProperty("label").GetString()! : valueBand.GetProperty("label").GetString()!))
                : valueBand.GetProperty("label").GetString()!);

        // Guidance of the worst band(s) only (CONTRACTS.md §2 rule 5); a normal half must not dilute the advice.
        var guidance = new List<string>();
        if (ParseStatus(valueBand.GetProperty("status").GetString()!) == status) AddGuidance(guidance, valueBand);
        if (secondaryBand is not null && ParseStatus(secondaryBand.Value.GetProperty("status").GetString()!) == status)
        {
            AddGuidance(guidance, secondaryBand.Value);
        }

        var escalation = status is HealthStatus.Watch or HealthStatus.Critical
            ? BuildEscalation(status)
            : null;

        var sources = BuildSources(indicator);

        return new ReadingEvaluation(
            input.Indicator,
            status,
            bandIds,
            label,
            guidance.Distinct().ToList(),
            escalation,
            sources);
    }

    private ReadingEvaluation BuildReadingEvaluation(IndicatorType indicator, JsonElement band, JsonElement indicatorElement, bool single)
    {
        var status = ParseStatus(band.GetProperty("status").GetString()!);
        var guidance = new List<string>();
        AddGuidance(guidance, band);

        return new ReadingEvaluation(
            indicator,
            status,
            new[] { band.GetProperty("id").GetString()! },
            band.GetProperty("label").GetString()!,
            guidance,
            status is HealthStatus.Watch or HealthStatus.Critical ? BuildEscalation(status) : null,
            BuildSources(indicatorElement));
    }

    private static JsonElement? FindBand(JsonElement bands, decimal value)
    {
        foreach (var band in bands.EnumerateArray())
        {
            decimal? min = band.TryGetProperty("min", out var minEl) ? minEl.GetDecimal() : null;
            decimal? max = band.TryGetProperty("max", out var maxEl) ? maxEl.GetDecimal() : null;

            if ((min is null || value >= min) && (max is null || value < max))
            {
                return band;
            }
        }

        return null;
    }

    private static HealthStatus ParseStatus(string status) => status switch
    {
        "normal" => HealthStatus.Normal,
        "watch" => HealthStatus.Watch,
        "critical" => HealthStatus.Critical,
        _ => HealthStatus.Normal,
    };

    private static void AddGuidance(List<string> guidance, JsonElement band)
    {
        if (band.TryGetProperty("guidance", out var g))
        {
            foreach (var item in g.EnumerateArray())
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    guidance.Add(text!);
                }
            }
        }
    }

    private Escalation BuildEscalation(HealthStatus status)
    {
        var key = status == HealthStatus.Critical ? "critical" : "watch";
        var esc = _document.RootElement.GetProperty("escalation").GetProperty(key);
        var contact = esc.GetProperty("contact").GetString() == "crewMedicalOfficer"
            ? EscalationContact.CrewMedicalOfficer
            : EscalationContact.Self;
        return new Escalation(contact, esc.GetProperty("message").GetString()!);
    }

    private List<SourceLink> BuildSources(JsonElement indicator)
    {
        var sourcesRoot = _document.RootElement.GetProperty("sources");
        var result = new List<SourceLink>();
        if (indicator.TryGetProperty("sources", out var srcs))
        {
            foreach (var key in srcs.EnumerateArray())
            {
                var k = key.GetString()!;
                if (sourcesRoot.TryGetProperty(k, out var source))
                {
                    result.Add(new SourceLink(
                        source.GetProperty("title").GetString()!,
                        new Uri(source.GetProperty("url").GetString()!)));
                }
            }
        }

        return result;
    }

    private static void ValidateRange(JsonElement range, decimal value, string name, List<string> errors)
    {
        if (range.TryGetProperty("min", out var minEl))
        {
            var min = minEl.GetDecimal();
            if (value < min)
            {
                errors.Add($"{name} is below the valid range.");
            }
        }

        if (range.TryGetProperty("max", out var maxEl))
        {
            var max = maxEl.GetDecimal();
            if (value > max)
            {
                errors.Add($"{name} is above the valid range.");
            }
        }
    }

    private static string ToIndicatorId(IndicatorType type)
    {
        var name = type.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}