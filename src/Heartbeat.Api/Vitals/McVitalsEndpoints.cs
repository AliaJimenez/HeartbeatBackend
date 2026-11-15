using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Vitals;

/// <summary>Mission Control view of an astronaut's vitals (CONTRACTS.md §6, MC-VITALS).</summary>
public static class McVitalsEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapMcVitalsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mc/v1/crew/{id:guid}");

        group.MapGet("/status", GetStatusAsync).RequirePermission(Permissions.VitalsView);
        group.MapGet("/checkups", ListCheckUpsAsync).RequirePermission(Permissions.VitalsHistory);
        group.MapGet("/checkups/{checkUpId:guid}", GetCheckUpAsync).RequirePermission(Permissions.VitalsView);
        group.MapGet("/series", GetSeriesAsync).RequirePermission(Permissions.VitalsHistory);
        group.MapGet("/radiation", GetRadiationAsync).RequirePermission(Permissions.VitalsView);
        group.MapGet("/environment", GetEnvironmentAsync).RequirePermission(Permissions.VitalsView);
        group.MapGet("/risk", GetRiskAsync).RequirePermission(Permissions.VitalsView);

        return app;
    }

    private static async Task<IResult> GetRiskAsync(
        Guid id, AppDbContext db, CancellationToken ct, decimal hoursWithoutContact = 0m, decimal continuousStayHours = 0m)
    {
        if (Crew.McMissionEndpoints.ValidateHours(hoursWithoutContact, continuousStayHours) is { } invalid)
        {
            return invalid;
        }

        if (!await db.Astronauts.AnyAsync(a => a.Id == id, ct))
        {
            return AstronautNotFound();
        }

        return Results.Ok(await Crew.RiskStatuses.LoadAsync(db, id, hoursWithoutContact, continuousStayHours, ct));
    }

    private static async Task<IResult> GetEnvironmentAsync(
        Guid id, AppDbContext db, CancellationToken ct,
        string? indicator = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int page = 1, int pageSize = DefaultPageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (indicator is not null && Crew.EnvironmentReadings.EnvironmentIndicator(indicator) is null)
        {
            errors["indicator"] = ["indicator must be an environment indicator of the catalog (co2, o2, pressure, temperature, humidity, light, noise)."];
        }

        if (page < 1)
        {
            errors["page"] = ["page must be 1 or greater."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
        }

        if (from is not null && to is not null && from > to)
        {
            errors["from"] = ["from must not be after to."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (!await db.Astronauts.AnyAsync(a => a.Id == id, ct))
        {
            return AstronautNotFound();
        }

        return Results.Ok(await Crew.EnvironmentReadings.LoadAsync(db, id, indicator, from, to, page, pageSize, ct));
    }

    private static async Task<IResult> GetRadiationAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        if (!await db.Astronauts.AnyAsync(a => a.Id == id, ct))
        {
            return AstronautNotFound();
        }

        return Results.Ok(await Crew.RadiationSummaries.LoadAsync(db, id, ct));
    }

    // SQLite cannot compare or ORDER BY DateTimeOffset in SQL, so check-ups are filtered, ordered and paged in
    // memory from a thin projection; only the requested page loads its readings.
    private sealed record CheckUpRow(Guid Id, DateTimeOffset RecordedAt, DateTimeOffset? ReceivedAt, HealthStatus OverallStatus, string RangesVersion, string? Note, Guid? SupersedesId);

    private static async Task<List<CheckUpRow>> LoadActiveCheckUpsAsync(AppDbContext db, Guid astronautId, CancellationToken ct)
    {
        var rows = await db.CheckUps.AsNoTracking()
            .Where(c => c.AstronautId == astronautId)
            .Select(c => new CheckUpRow(c.Id, c.RecordedAt, c.ReceivedAt, c.OverallStatus, c.RangesVersion, c.Note, c.SupersedesId))
            .ToListAsync(ct);

        // Corrections replace the check-up they supersede; history and dashboards hide the replaced one.
        var superseded = rows.Where(r => r.SupersedesId != null).Select(r => r.SupersedesId!.Value).ToHashSet();
        return rows.Where(r => !superseded.Contains(r.Id)).OrderByDescending(r => r.RecordedAt).ToList();
    }

    private static async Task<IResult> GetStatusAsync(Guid id, AppDbContext db, RangesCatalog ranges, CancellationToken ct)
    {
        if (!await db.Astronauts.AnyAsync(a => a.Id == id, ct))
        {
            return AstronautNotFound();
        }

        var active = await LoadActiveCheckUpsAsync(db, id, ct);
        if (active.Count == 0)
        {
            return Results.Ok(new AstronautStatusDto(id, null, null, []));
        }

        var activeIds = active.Select(c => c.Id).ToList();
        var readings = await db.Readings.AsNoTracking().Where(r => activeIds.Contains(r.CheckUpId)).ToListAsync(ct);
        var recordedAt = active.ToDictionary(c => c.Id, c => c.RecordedAt);

        var indicators = readings
            .GroupBy(r => r.Indicator)
            .Select(g => g.OrderByDescending(r => recordedAt[r.CheckUpId]).First())
            .OrderBy(r => r.Indicator)
            .Select(r => new IndicatorStatusDto(
                r.Indicator,
                r.Value,
                r.SecondaryValue,
                ranges.UnitFor(r.Indicator),
                r.Status,
                ranges.LabelFor(r.Indicator, SplitBandIds(r.BandIds)),
                recordedAt[r.CheckUpId],
                r.CheckUpId))
            .ToList();

        return Results.Ok(new AstronautStatusDto(
            id,
            active[0].OverallStatus,
            active.Where(c => c.ReceivedAt != null).Select(c => c.ReceivedAt).Max(),
            indicators));
    }

    private static async Task<IResult> ListCheckUpsAsync(
        Guid id,
        AppDbContext db,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? status = null,
        int page = 1,
        int pageSize = DefaultPageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
        {
            errors["page"] = ["page must be 1 or greater."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
        }

        if (from is not null && to is not null && from > to)
        {
            errors["from"] = ["from must not be later than to."];
        }

        // Parsed by hand: query-string enum binding is not reliably case-insensitive.
        HealthStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (Enum.TryParse<HealthStatus>(status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                errors["status"] = ["status must be normal, watch or critical."];
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (!await db.Astronauts.AnyAsync(a => a.Id == id, ct))
        {
            return AstronautNotFound();
        }

        var filtered = (await LoadActiveCheckUpsAsync(db, id, ct))
            .Where(c => (from is null || c.RecordedAt >= from)
                        && (to is null || c.RecordedAt <= to)
                        && (statusFilter is null || c.OverallStatus == statusFilter))
            .ToList();

        var pageRows = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var pageIds = pageRows.Select(c => c.Id).ToList();
        var readings = (await db.Readings.AsNoTracking().Where(r => pageIds.Contains(r.CheckUpId)).ToListAsync(ct))
            .ToLookup(r => r.CheckUpId);

        var items = pageRows.Select(row => ToDto(row, readings[row.Id])).ToList();

        return Results.Ok(new Paged<CheckUpDto>(items, page, pageSize, filtered.Count));
    }

    private static async Task<IResult> GetCheckUpAsync(Guid id, Guid checkUpId, AppDbContext db, RangesCatalog ranges, CancellationToken ct)
    {
        var checkUp = await db.CheckUps.AsNoTracking()
            .Include(c => c.Readings)
            .FirstOrDefaultAsync(c => c.Id == checkUpId && c.AstronautId == id, ct);

        if (checkUp is null)
        {
            return Results.Problem(title: "Check-up not found", statusCode: StatusCodes.Status404NotFound);
        }

        var readings = checkUp.Readings.OrderBy(r => r.Indicator).ToList();
        var evaluations = readings
            .Select(r => ranges.Evaluate(r.Indicator, r.Status, SplitBandIds(r.BandIds)))
            .ToList();

        return Results.Ok(new CheckUpDetailDto(
            checkUp.Id,
            checkUp.RecordedAt,
            checkUp.ReceivedAt,
            checkUp.OverallStatus,
            checkUp.RangesVersion,
            checkUp.Note,
            checkUp.SupersedesId,
            readings.Select(ToDto).ToList(),
            evaluations));
    }

    private static async Task<IResult> GetSeriesAsync(
        Guid id,
        AppDbContext db,
        RangesCatalog ranges,
        CancellationToken ct,
        string? indicator = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var errors = new Dictionary<string, string[]>();

        // Parsed by hand: query-string enum binding is not reliably case-insensitive.
        IndicatorType parsedIndicator = default;
        if (string.IsNullOrWhiteSpace(indicator)
            || !Enum.TryParse(indicator, ignoreCase: true, out parsedIndicator)
            || !Enum.IsDefined(parsedIndicator))
        {
            errors["indicator"] = [$"indicator is required and must be one of: {string.Join(", ", Enum.GetNames<IndicatorType>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]))}."];
        }

        if (from is not null && to is not null && from > to)
        {
            errors["from"] = ["from must not be later than to."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (!await db.Astronauts.AnyAsync(a => a.Id == id, ct))
        {
            return AstronautNotFound();
        }

        var active = (await LoadActiveCheckUpsAsync(db, id, ct))
            .Where(c => (from is null || c.RecordedAt >= from) && (to is null || c.RecordedAt <= to))
            .ToDictionary(c => c.Id, c => c.RecordedAt);
        var activeIds = active.Keys.ToList();

        var readings = await db.Readings.AsNoTracking()
            .Where(r => r.Indicator == parsedIndicator && activeIds.Contains(r.CheckUpId))
            .ToListAsync(ct);

        var points = readings
            .OrderBy(r => active[r.CheckUpId])
            .Select(r => new SeriesPointDto(active[r.CheckUpId], r.Value, r.SecondaryValue, r.Status))
            .ToList();

        return Results.Ok(new SeriesDto(parsedIndicator, ranges.UnitFor(parsedIndicator), ranges.BandsFor(parsedIndicator), points));
    }

    private static CheckUpDto ToDto(CheckUpRow row, IEnumerable<Reading> readings) => new(
        row.Id,
        row.RecordedAt,
        row.ReceivedAt,
        row.OverallStatus,
        row.RangesVersion,
        row.Note,
        row.SupersedesId,
        readings.OrderBy(r => r.Indicator).Select(ToDto).ToList());

    private static ReadingDto ToDto(Reading r) => new(r.Id, r.Indicator, r.Value, r.SecondaryValue, r.Status, SplitBandIds(r.BandIds), r.Source, r.DeviceId);

    private static string[] SplitBandIds(string bandIds)
        => bandIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IResult AstronautNotFound() => Results.Problem(title: "Astronaut not found", statusCode: StatusCodes.Status404NotFound);
}
