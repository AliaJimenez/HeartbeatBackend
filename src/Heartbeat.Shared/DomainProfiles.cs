using System.Text.Json;

namespace Heartbeat.Shared;

/// <summary>
/// A domain profile (HB-BE-11): a typical environment plus per-indicator replacements of the catalog bands.
/// Everything else (hazards, indicators, units) is the catalog's, so the five hazards are reused with other thresholds.
/// </summary>
public sealed record DomainProfile(
    string Id,
    string Name,
    string Description,
    EnvironmentProfile Environment,
    string SourceId,
    CatalogStatus Status,
    IReadOnlyDictionary<string, IReadOnlyList<IndicatorBand>> Overrides);

/// <summary>
/// The profiles of <c>contracts/indicators.catalog.json</c> (<c>profiles</c>). Thresholds live in that file, never in
/// the UI. The <c>space</c> profile has no overrides, so it is the catalog itself.
/// </summary>
public static class DomainProfiles
{
    public const string SpaceId = "space";

    private static readonly Lazy<IReadOnlyList<DomainProfile>> _all = new(Load);

    public static IReadOnlyList<DomainProfile> All => _all.Value;

    public static DomainProfile? Find(string? id)
        => id is null ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The catalog entries as the profile sees them: overridden bands, with the profile's status and source.</summary>
    public static IReadOnlyList<IndicatorCatalogEntry> Effective(DomainProfile profile)
        => IndicatorsCatalog.Cached
            .Select(e => profile.Overrides.TryGetValue(e.Id, out var bands)
                ? e with { Bands = bands, Source = profile.SourceId, Status = profile.Status }
                : e)
            .ToList();

    /// <summary>The band a value falls in for an indicator under a profile.</summary>
    public static IndicatorBand Evaluate(DomainProfile profile, string indicatorId, decimal value)
    {
        var entry = Effective(profile).FirstOrDefault(e => string.Equals(e.Id, indicatorId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"The indicators catalog has no '{indicatorId}' entry.", nameof(indicatorId));
        return IndicatorsCatalog.Evaluate(entry, value);
    }

    /// <summary>A catalog entry as the profile sees it; with no profile, the plain catalog entry.</summary>
    public static IndicatorCatalogEntry? EntryFor(DomainProfile? profile, string? indicatorId)
    {
        var entry = IndicatorsCatalog.Find(indicatorId);
        if (entry is null || profile is null || !profile.Overrides.TryGetValue(entry.Id, out var bands))
        {
            return entry;
        }

        return entry with { Bands = bands, Source = profile.SourceId, Status = profile.Status };
    }

    /// <summary>
    /// The same habitat or vision reading judged with the profile's thresholds (status, label, catalog status and
    /// source). The value itself never changes. Without a profile, or for an indicator it does not override, it
    /// is returned as it is.
    /// </summary>
    public static EnvironmentReadingStatus Apply(DomainProfile? profile, EnvironmentReadingStatus reading)
    {
        if (profile is null || !profile.Overrides.ContainsKey(reading.IndicatorId))
        {
            return reading;
        }

        var entry = EntryFor(profile, reading.IndicatorId)!;
        var band = IndicatorsCatalog.Evaluate(entry, reading.Value);
        return reading with
        {
            Status = band.Status,
            Label = string.IsNullOrWhiteSpace(band.Label) ? band.Status.ToString().ToLowerInvariant() : band.Label,
            CatalogStatus = entry.Status,
            Source = IndicatorsCatalog.SourceFor(entry),
        };
    }

    public static DomainProfileDto ToDto(DomainProfile profile)
    {
        var effective = Effective(profile);
        var hazards = Enum.GetValues<Hazard>()
            .Select(hazard => new ProfileHazardDto(
                hazard,
                effective.Where(e => e.Hazard == hazard)
                    .Select(e => new ProfileIndicatorDto(
                        e.Id,
                        e.Name,
                        e.Unit,
                        e.Status,
                        IndicatorsCatalog.SourceFor(e),
                        profile.Overrides.ContainsKey(e.Id),
                        e.Bands.OrderBy(b => b.Min ?? decimal.MinValue)
                            .Select(b => new ProfileBandDto(b.Status, b.Min, b.Max))
                            .ToList()))
                    .ToList()))
            .ToList();

        return new DomainProfileDto(
            profile.Id, profile.Name, profile.Description, profile.Environment, profile.Status,
            IndicatorsCatalog.SourceFor(profile.SourceId), hazards);
    }

    private static IReadOnlyList<DomainProfile> Load()
    {
        using var doc = IndicatorsCatalog.Parse();
        var root = doc.RootElement;
        var sources = root.GetProperty("sources");
        var known = IndicatorsCatalog.Cached.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profiles = new List<DomainProfile>();

        foreach (var p in root.GetProperty("profiles").EnumerateArray())
        {
            var id = p.GetProperty("id").GetString()!;
            var sourceId = p.GetProperty("source").GetString()!;
            if (!sources.TryGetProperty(sourceId, out _))
            {
                throw new InvalidOperationException($"Profile '{id}' references an unknown source '{sourceId}'.");
            }

            var env = p.GetProperty("environment");
            var environment = new EnvironmentProfile(
                env.GetProperty("type").GetString()!,
                env.GetProperty("gravityG").GetDecimal(),
                env.GetProperty("distanceKm").GetDecimal(),
                env.GetProperty("crewSize").GetInt32(),
                env.GetProperty("habitableVolumeM3").GetDecimal());

            var overrides = new Dictionary<string, IReadOnlyList<IndicatorBand>>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in p.GetProperty("overrides").EnumerateObject())
            {
                if (!known.Contains(o.Name))
                {
                    throw new InvalidOperationException($"Profile '{id}' overrides an unknown indicator '{o.Name}'.");
                }

                var bands = o.Value.EnumerateArray()
                    .Select(b => new IndicatorBand(
                        string.Empty,
                        Enum.Parse<HealthStatus>(b.GetProperty("status").GetString()!, ignoreCase: true),
                        b.TryGetProperty("min", out var min) ? min.GetDecimal() : null,
                        b.TryGetProperty("max", out var max) ? max.GetDecimal() : null,
                        string.Empty))
                    .ToList();
                IndicatorsCatalog.ValidateBands($"{id}/{o.Name}", bands);
                overrides[o.Name] = bands;
            }

            profiles.Add(new DomainProfile(
                id,
                p.GetProperty("name").GetString()!,
                p.GetProperty("description").GetString()!,
                environment,
                sourceId,
                Enum.Parse<CatalogStatus>(p.GetProperty("status").GetString()!, ignoreCase: true),
                overrides));
        }

        if (profiles.Count == 0 || profiles.All(p => p.Id != SpaceId))
        {
            throw new InvalidOperationException($"The catalog must define the '{SpaceId}' reference profile.");
        }

        return profiles;
    }
}
