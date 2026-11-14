using System.Text.Json;

namespace Heartbeat.Shared;

public sealed class HealthEvaluator : IHealthEvaluator
{
    private readonly Dictionary<IndicatorType, IndicatorInfo> _indicators;
    private readonly EscalationContact _watchContact;
    private readonly string _watchMessage;
    private readonly EscalationContact _criticalContact;
    private readonly string _criticalMessage;

    public HealthEvaluator() : this(ReferenceRanges.Json) { }

    public HealthEvaluator(string rangesJson)
    {
        using var doc = JsonDocument.Parse(rangesJson);
        var root = doc.RootElement;

        RangesVersion = root.GetProperty("version").GetString() ?? "unknown";

        var esc = root.GetProperty("escalation");
        _watchContact = ParseContact(esc.GetProperty("watch").GetProperty("contact").GetString()!);
        _watchMessage = esc.GetProperty("watch").GetProperty("message").GetString()!;
        _criticalContact = ParseContact(esc.GetProperty("critical").GetProperty("contact").GetString()!);
        _criticalMessage = esc.GetProperty("critical").GetProperty("message").GetString()!;

        var sourcesMap = root.GetProperty("sources");

        _indicators = new Dictionary<IndicatorType, IndicatorInfo>();
        foreach (var ind in root.GetProperty("indicators").EnumerateArray())
        {
            var id = ind.GetProperty("id").GetString()!;
            var type = ParseIndicator(id);

            var validRange = ind.GetProperty("validRange");
            var valueRange = ParseRange(validRange.GetProperty("value"));
            (decimal? Min, decimal? Max) secondaryRange = (null, null);
            if (validRange.TryGetProperty("secondaryValue", out var svr))
                secondaryRange = ParseRange(svr);

            var sourceKeys = ind.GetProperty("sources").EnumerateArray()
                .Select(x => x.GetString()!).ToList();
            var sources = sourceKeys.Select(k =>
            {
                var s = sourcesMap.GetProperty(k);
                return new SourceLink(s.GetProperty("title").GetString()!, new Uri(s.GetProperty("url").GetString()!));
            }).ToList();

            var eval = ind.GetProperty("evaluation").GetString()!;
            List<Band> valueBands;
            List<Band> secondaryBands = new();
            if (eval == "worstOfComponents")
            {
                var comps = ind.GetProperty("components");
                valueBands = ParseBands(comps.GetProperty("value"));
                secondaryBands = ParseBands(comps.GetProperty("secondaryValue"));
            }
            else
            {
                valueBands = ParseBands(ind.GetProperty("bands"));
            }

            _indicators[type] = new IndicatorInfo
            {
                Type = type,
                Id = id,
                ValueRange = valueRange,
                SecondaryRange = secondaryRange,
                Sources = sources,
                Evaluation = eval,
                ValueBands = valueBands,
                SecondaryBands = secondaryBands,
            };
        }
    }

    public string RangesVersion { get; }

    public ReadingEvaluation Evaluate(ReadingInput input)
    {
        var info = _indicators[input.Indicator];

        if (info.Evaluation == "worstOfComponents")
        {
            var sysBand = FindBand(info.ValueBands, input.Value);
            var diaValue = input.SecondaryValue ?? 0m;
            var diaBand = FindBand(info.SecondaryBands, diaValue);

            var status = sysBand.Status >= diaBand.Status ? sysBand.Status : diaBand.Status;

            string label;
            IReadOnlyList<string> guidance;
            if (status == sysBand.Status && status == diaBand.Status)
            {
                label = sysBand.Label;
                guidance = Dedup(sysBand.Guidance.Concat(diaBand.Guidance));
            }
            else if (status == sysBand.Status)
            {
                label = sysBand.Label;
                guidance = sysBand.Guidance;
            }
            else
            {
                label = diaBand.Label;
                guidance = diaBand.Guidance;
            }

            var bandIds = new[] { sysBand.Id, diaBand.Id };
            var escalation = BuildEscalation(status);
            return new ReadingEvaluation(input.Indicator, status, bandIds, label, guidance, escalation, info.Sources);
        }

        var band = FindBand(info.ValueBands, input.Value);
        var esc = BuildEscalation(band.Status);
        return new ReadingEvaluation(input.Indicator, band.Status, new[] { band.Id }, band.Label, band.Guidance, esc, info.Sources);
    }

    public CheckUpEvaluation Evaluate(IEnumerable<ReadingInput> inputs)
    {
        var readings = inputs.Select(Evaluate).ToList();
        var overall = readings.Count == 0 ? HealthStatus.Normal : readings.Max(r => r.Status);
        return new CheckUpEvaluation(overall, readings, RangesVersion);
    }

    public IReadOnlyList<string> Validate(ReadingInput input)
    {
        var info = _indicators[input.Indicator];
        var errors = new List<string>();

        var (vMin, vMax) = info.ValueRange;
        if (vMin.HasValue && input.Value < vMin.Value)
            errors.Add($"Value must be between {vMin} and {vMax}.");
        if (vMax.HasValue && input.Value > vMax.Value)
            errors.Add($"Value must be between {vMin} and {vMax}.");

        if (info.Evaluation == "worstOfComponents")
        {
            if (input.SecondaryValue is null)
            {
                errors.Add("SecondaryValue is required.");
            }
            else
            {
                var (sMin, sMax) = info.SecondaryRange;
                if (sMin.HasValue && input.SecondaryValue.Value < sMin.Value)
                    errors.Add($"SecondaryValue must be between {sMin} and {sMax}.");
                if (sMax.HasValue && input.SecondaryValue.Value > sMax.Value)
                    errors.Add($"SecondaryValue must be between {sMin} and {sMax}.");
            }
        }

        return errors;
    }

    private Escalation? BuildEscalation(HealthStatus status) => status switch
    {
        HealthStatus.Normal => null,
        HealthStatus.Watch => new Escalation(_watchContact, _watchMessage),
        HealthStatus.Critical => new Escalation(_criticalContact, _criticalMessage),
        _ => null,
    };

    private static Band FindBand(List<Band> bands, decimal value)
    {
        foreach (var b in bands)
        {
            if ((b.Min is null || value >= b.Min.Value) && (b.Max is null || value < b.Max.Value))
                return b;
        }
        // Fallback: last band
        return bands[^1];
    }

    private static IReadOnlyList<string> Dedup(IEnumerable<string> items)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var i in items)
        {
            if (seen.Add(i)) result.Add(i);
        }
        return result;
    }

    private static IndicatorType ParseIndicator(string id) => id switch
    {
        "bloodPressure" => IndicatorType.BloodPressure,
        "heartRate" => IndicatorType.HeartRate,
        "bloodSugar" => IndicatorType.BloodSugar,
        "bodyTemperature" => IndicatorType.BodyTemperature,
        "sleep" => IndicatorType.Sleep,
        "mood" => IndicatorType.Mood,
        _ => throw new ArgumentException($"Unknown indicator id: {id}"),
    };

    private static HealthStatus ParseStatus(string s) => s switch
    {
        "normal" => HealthStatus.Normal,
        "watch" => HealthStatus.Watch,
        "critical" => HealthStatus.Critical,
        _ => throw new ArgumentException($"Unknown status: {s}"),
    };

    private static EscalationContact ParseContact(string s) => s switch
    {
        "self" => EscalationContact.Self,
        "crewMedicalOfficer" => EscalationContact.CrewMedicalOfficer,
        _ => throw new ArgumentException($"Unknown contact: {s}"),
    };

    private static (decimal? Min, decimal? Max) ParseRange(JsonElement e)
    {
        decimal? min = e.TryGetProperty("min", out var m) ? m.GetDecimal() : null;
        decimal? max = e.TryGetProperty("max", out var x) ? x.GetDecimal() : null;
        return (min, max);
    }

    private static List<Band> ParseBands(JsonElement arr)
    {
        var list = new List<Band>();
        foreach (var b in arr.EnumerateArray())
        {
            var status = ParseStatus(b.GetProperty("status").GetString()!);
            decimal? min = b.TryGetProperty("min", out var m) ? m.GetDecimal() : null;
            decimal? max = b.TryGetProperty("max", out var x) ? x.GetDecimal() : null;
            var guidance = b.GetProperty("guidance").EnumerateArray()
                .Select(g => g.GetString()!).ToList();
            list.Add(new Band(
                b.GetProperty("id").GetString()!,
                status,
                min,
                max,
                b.GetProperty("label").GetString()!,
                guidance));
        }
        return list;
    }

    private sealed record Band(string Id, HealthStatus Status, decimal? Min, decimal? Max, string Label, IReadOnlyList<string> Guidance);

    private sealed class IndicatorInfo
    {
        public IndicatorType Type { get; set; }
        public string Id { get; set; } = string.Empty;
        public (decimal? Min, decimal? Max) ValueRange { get; set; }
        public (decimal? Min, decimal? Max) SecondaryRange { get; set; }
        public IReadOnlyList<SourceLink> Sources { get; set; } = Array.Empty<SourceLink>();
        public string Evaluation { get; set; } = string.Empty;
        public List<Band> ValueBands { get; set; } = new();
        public List<Band> SecondaryBands { get; set; } = new();
    }
}