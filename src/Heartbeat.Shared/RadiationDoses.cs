using System.Text.Json;

namespace Heartbeat.Shared;

public static class RadiationDoses
{
    // Limits from the NASA Radiation Protection Technical Brief (see the csa-radiation / nasa-radiation-brief sources).
    public const string SourceId = "csa-radiation";

    public const decimal CareerLimitMsv = 600m;
    public const decimal SpeLimitMsv = 250m;

    public const decimal MaxEntryMsv = 500m;

    public const int NoteMaxLength = 200;

    public const decimal GcrRateMsvPerDayStation = 0.5m;
    public const decimal GcrRateMsvPerDayMoon = 1.3m;
    public const decimal GcrRateMsvPerDayMars = 0.7m;
    public const decimal GcrRateMsvPerDayDeepSpace = 1.8m;

    public static decimal RateForEnvironment(string environment) => environment.ToLowerInvariant() switch
    {
        "station" or "spacecraft" => GcrRateMsvPerDayStation,
        "lunarbase" or "lunar" => GcrRateMsvPerDayMoon,
        "marsbase" or "mars" => GcrRateMsvPerDayMars,
        _ => GcrRateMsvPerDayDeepSpace,
    };

    public static IReadOnlyList<string> Validate(decimal doseMsv, string? note)
    {
        var errors = new List<string>();

        if (doseMsv <= 0)
        {
            errors.Add("Radiation dose must be greater than 0 mSv");
        }
        else if (doseMsv > MaxEntryMsv)
        {
            errors.Add($"Radiation dose cannot exceed {MaxEntryMsv:0} mSv per entry");
        }

        if (note is { Length: > NoteMaxLength })
        {
            errors.Add($"Radiation note cannot exceed {NoteMaxLength} characters");
        }

        return errors;
    }

    public static decimal Total(IEnumerable<decimal> dosesMsv) => dosesMsv.Sum();

    public static RadiationType Classify(decimal doseMsv, bool isSolarEvent) =>
        isSolarEvent ? RadiationType.Spe : RadiationType.Gcr;

    // Stored in Alert.Indicators for radiation alerts (they have no IndicatorType).
    public const string AlertSpe = "radiation-spe";
    public const string AlertCumulative = "radiation-cumulative";

    public static string SpeAlertMessage(decimal doseMsv) =>
        $"Solar particle event (simulated): {doseMsv:0.##} mSv, {PercentOfSpeLimit(doseMsv):0.#}% of the {SpeLimitMsv:0} mSv per-event limit.";

    public static string CumulativeAlertMessage(decimal totalMsv) =>
        $"Cumulative radiation (simulated) reached {totalMsv:0.##} mSv, {PercentOfCareerLimit(totalMsv):0.#}% of the {CareerLimitMsv:0} mSv career limit.";

    public static decimal PercentOfCareerLimit(decimal totalMsv) =>
        CareerLimitMsv == 0 ? 0 : Math.Round(totalMsv / CareerLimitMsv * 100m, 2, MidpointRounding.AwayFromZero);

    public static decimal PercentOfSpeLimit(decimal speDoseMsv) =>
        SpeLimitMsv == 0 ? 0 : Math.Round(speDoseMsv / SpeLimitMsv * 100m, 2, MidpointRounding.AwayFromZero);

    public static HealthStatus StatusForCareerPercent(decimal percent) => percent switch
    {
        >= 100 => HealthStatus.Critical,
        >= 50 => HealthStatus.Watch,
        _ => HealthStatus.Normal,
    };

    public static HealthStatus WorstOf(HealthStatus a, HealthStatus b) => (HealthStatus)Math.Max((int)a, (int)b);

    public static HealthStatus StatusForSpePercent(decimal percent) => percent switch
    {
        >= 100 => HealthStatus.Critical,
        >= 50 => HealthStatus.Watch,
        _ => HealthStatus.Normal,
    };
}
