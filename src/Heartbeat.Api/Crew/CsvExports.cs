using System.Globalization;
using System.Text;
using System.Text.Json;
using Heartbeat.Api.Data;
using Heartbeat.Api.Vitals;
using Heartbeat.Shared;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Crew;

/// <summary>
/// CSV report per astronaut and per mission (HB-BE-10). One tidy table, one row per record, so it opens in any
/// spreadsheet: the risk traffic lights as of the export, the vital signs of every check-up, the radiation doses and the
/// habitat and vision readings. Free text written by people (check-up notes, messages, profile notes) and the date of
/// birth are never exported. The rows that come from simulated models and readings say so in the last column.
/// </summary>
public static class CsvExports
{
    public const string ContentType = "text/csv; charset=utf-8";

    public static readonly string[] Header =
    [
        "astronaut_id", "astronaut", "record_type", "recorded_at", "indicator", "value", "secondary_value",
        "unit", "status", "detail", "source", "catalog_status", "simulated",
    ];

    public sealed record Person(Guid Id, string DisplayName);

    private sealed record Row(
        Person Astronaut, string Type, DateTimeOffset At, string Indicator, decimal? Value, decimal? Secondary,
        string Unit, string Status, string Detail, string Source, string CatalogStatus, bool Simulated);

    private static readonly string[] TypeOrder = ["risk", "vital", "radiation", "environment"];

    private static readonly Lazy<string> VitalsCatalogStatus = new(() =>
    {
        using var doc = ReferenceRanges.Parse();
        return doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? string.Empty : string.Empty;
    });

    /// <summary>The whole report for these astronauts, restricted to the period (it filters the dated records, not the risk summary).</summary>
    public static async Task<string> BuildAsync(
        AppDbContext db,
        RangesCatalog ranges,
        IReadOnlyList<Person> astronauts,
        DateTimeOffset? from,
        DateTimeOffset? to,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ids = astronauts.Select(a => a.Id).ToList();
        var byId = astronauts.ToDictionary(a => a.Id);
        bool InPeriod(DateTimeOffset at) => (from is null || at >= from) && (to is null || at <= to);
        var rows = new List<Row>();

        // Vital signs: every reading of every check-up that still counts (a correction replaces the check-up it supersedes).
        var checkUps = (await db.CheckUps.AsNoTracking()
                .Where(c => ids.Contains(c.AstronautId))
                .Select(c => new { c.Id, c.AstronautId, c.RecordedAt, c.SupersedesId })
                .ToListAsync(cancellationToken))
            .ToList();
        var superseded = checkUps.Where(c => c.SupersedesId != null).Select(c => c.SupersedesId!.Value).ToHashSet();
        var active = checkUps.Where(c => !superseded.Contains(c.Id) && InPeriod(c.RecordedAt)).ToDictionary(c => c.Id);
        var activeIds = active.Keys.ToList();
        var readings = activeIds.Count == 0
            ? []
            : await db.Readings.AsNoTracking().Where(r => activeIds.Contains(r.CheckUpId)).ToListAsync(cancellationToken);
        foreach (var r in readings)
        {
            var checkUp = active[r.CheckUpId];
            var evaluation = ranges.Evaluate(r.Indicator, r.Status, r.BandIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            rows.Add(new Row(
                byId[checkUp.AstronautId], "vital", checkUp.RecordedAt, Camel(r.Indicator.ToString()), r.Value, r.SecondaryValue,
                ranges.UnitFor(r.Indicator), RiskEngine.Word(r.Status), evaluation.Label, evaluation.Sources.FirstOrDefault()?.Title ?? string.Empty,
                VitalsCatalogStatus.Value, Simulated: false));
        }

        // Radiation doses.
        var radiationSource = ReferenceRanges.GetSource(RadiationDoses.SourceId).Title;
        var doses = await db.RadiationDoses.AsNoTracking().Where(d => ids.Contains(d.AstronautId)).ToListAsync(cancellationToken);
        foreach (var d in doses.Where(d => InPeriod(d.RecordedAt)))
        {
            rows.Add(new Row(byId[d.AstronautId], "radiation", d.RecordedAt, "radiationDose", d.DoseMsv, null, "mSv", string.Empty,
                Camel(d.Type.ToString()), radiationSource, "verified", Simulated: true));
        }

        // Habitat readings and IOP / visual acuity, evaluated with the bands of the indicators catalog.
        var environment = await db.EnvironmentReadings.AsNoTracking()
            .Where(r => r.AstronautId != null && ids.Contains(r.AstronautId.Value))
            .ToListAsync(cancellationToken);
        foreach (var e in environment.Where(e => InPeriod(e.RecordedAt)))
        {
            if (EnvironmentReadings.ToStatus(e) is not { } status)
            {
                continue;
            }

            rows.Add(new Row(byId[e.AstronautId!.Value], "environment", e.RecordedAt, status.IndicatorId, status.Value, status.SecondaryValue,
                status.Unit, RiskEngine.Word(status.Status), status.Label, status.Source.Title, Camel(status.CatalogStatus.ToString()), Simulated: true));
        }

        // The traffic lights as of the export: one row per hazard and the global one.
        foreach (var person in astronauts)
        {
            var risk = await RiskStatuses.LoadAsync(db, person.Id, 0m, 0m, cancellationToken);
            rows.Add(new Row(person, "risk", now, "global", null, null, string.Empty, RiskEngine.Word(risk.Status), risk.Explanation,
                string.Empty, string.Empty, Simulated: true));
            foreach (var h in risk.Hazards)
            {
                rows.Add(new Row(person, "risk", now, Camel(h.Hazard.ToString()), h.Value, h.Limit, h.Unit ?? string.Empty,
                    RiskEngine.Word(h.Status), h.Explanation, h.Source?.Title ?? string.Empty, string.Empty, Simulated: true));
            }
        }

        var sb = new StringBuilder();
        AppendLine(sb, Header);
        foreach (var row in rows
                     .OrderBy(r => r.Astronaut.DisplayName, StringComparer.Ordinal)
                     .ThenBy(r => r.Astronaut.Id)
                     .ThenBy(r => Array.IndexOf(TypeOrder, r.Type))
                     .ThenBy(r => r.At)
                     .ThenBy(r => r.Indicator, StringComparer.Ordinal))
        {
            AppendLine(sb,
            [
                row.Astronaut.Id.ToString(), Text(row.Astronaut.DisplayName), row.Type, Utc(row.At), row.Indicator,
                Number(row.Value), Number(row.Secondary), row.Unit, row.Status, Text(row.Detail), Text(row.Source),
                row.CatalogStatus, row.Simulated ? "true" : "false",
            ]);
        }

        return sb.ToString();
    }

    /// <summary>The file as bytes: UTF-8 with a byte order mark so spreadsheets read accents correctly.</summary>
    public static byte[] ToBytes(string csv) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)];

    /// <summary>A file name safe for a Content-Disposition header: letters, digits, dot, dash and underscore only.</summary>
    public static string FileName(string label, DateTimeOffset now)
    {
        var safe = new string(label.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray()).Trim('-');
        return $"heartbeat-{(safe.Length == 0 ? "export" : safe)}-{now:yyyyMMdd}.csv";
    }

    // ---- CSV writing ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Free text for a cell. A spreadsheet runs a cell that starts with = + - @ (or a tab or return) as a formula, so
    /// those get a leading apostrophe. Numbers do not go through here, so a negative value stays a number.
    /// </summary>
    public static string Text(string value)
        => value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;

    public static string Number(decimal? value) => value is null ? string.Empty : value.Value.ToString("0.####", CultureInfo.InvariantCulture);

    public static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string Escape(string field)
        => field.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;

    private static void AppendLine(StringBuilder sb, IEnumerable<string> fields)
        => sb.Append(string.Join(',', fields.Select(Escape))).Append("\r\n");

    private static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);
}
