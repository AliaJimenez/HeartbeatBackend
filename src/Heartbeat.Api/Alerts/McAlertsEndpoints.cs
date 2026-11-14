using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Alerts;

/// <summary>Critical alerts for flight surgeons: list and acknowledge (CONTRACTS.md §6, MC-ALERTS / MR-06).</summary>
/// <remarks>
/// Alerts are created by the sync endpoint. Acknowledging stores who and when; the astronaut receives it on the
/// next sync through <c>inbound.acknowledgedAlerts</c>.
/// </remarks>
public static class McAlertsEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;
    private const int MaxNoteLength = 500;

    public static IEndpointRouteBuilder MapMcAlertsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mc/v1/alerts");

        group.MapGet("/", ListAsync).RequirePermission(Permissions.AlertsView);
        group.MapPost("/{id:guid}/acknowledge", AcknowledgeAsync).RequirePermission(Permissions.AlertsAcknowledge);

        return app;
    }

    private static async Task<IResult> ListAsync(
        AppDbContext db, CancellationToken ct, string? state = "open", int page = 1, int pageSize = DefaultPageSize)
    {
        var errors = new Dictionary<string, string[]>();
        var wanted = (string.IsNullOrWhiteSpace(state) ? "open" : state).ToLowerInvariant();
        if (wanted is not ("open" or "acknowledged" or "all"))
        {
            errors["state"] = ["state must be open, acknowledged or all."];
        }

        if (page < 1)
        {
            errors["page"] = ["page must be 1 or greater."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var query = db.Alerts.AsNoTracking().AsQueryable();
        query = wanted switch
        {
            "open" => query.Where(a => a.AcknowledgedAt == null),
            "acknowledged" => query.Where(a => a.AcknowledgedAt != null),
            _ => query,
        };

        // SQLite cannot ORDER BY DateTimeOffset in SQL, so newest-first ordering and paging happen in memory.
        var all = await query
            .Select(a => new Row(a.Id, a.AstronautId, a.Astronaut.DisplayName, a.CheckUpId, a.Indicators,
                a.CreatedAt, a.AcknowledgedAt, a.AcknowledgedByUserId, a.AcknowledgeNote))
            .ToListAsync(ct);

        var pageRows = all.OrderByDescending(r => r.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var names = await NamesAsync(db, pageRows.Select(r => r.AcknowledgedByUserId), ct);

        return Results.Ok(new Paged<AlertDto>(pageRows.Select(r => ToDto(r, names)).ToList(), page, pageSize, all.Count));
    }

    private static async Task<IResult> AcknowledgeAsync(
        Guid id, AcknowledgeAlertRequest request, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct)
    {
        var note = request.Note?.Trim() ?? string.Empty;
        if (note.Length is 0 or > MaxNoteLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["note"] = [$"Note is required and must be at most {MaxNoteLength} characters."],
            });
        }

        if (!Guid.TryParse(caller.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId))
        {
            return Results.Problem(title: "Invalid token", statusCode: StatusCodes.Status401Unauthorized);
        }

        var user = await db.PortalUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
        {
            return Results.Problem(title: "Portal user not found or disabled", statusCode: StatusCodes.Status401Unauthorized);
        }

        var alert = await db.Alerts.Include(a => a.Astronaut).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (alert is null)
        {
            return Results.Problem(title: "Alert not found", statusCode: StatusCodes.Status404NotFound);
        }

        if (alert.AcknowledgedAt is not null)
        {
            return Results.Problem(title: "The alert was already acknowledged", statusCode: StatusCodes.Status409Conflict);
        }

        alert.AcknowledgedAt = DateTimeOffset.UtcNow;
        alert.AcknowledgedByUserId = userId;
        alert.AcknowledgeNote = note;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToDto(
            new Row(alert.Id, alert.AstronautId, alert.Astronaut.DisplayName, alert.CheckUpId, alert.Indicators,
                alert.CreatedAt, alert.AcknowledgedAt, userId, note),
            new Dictionary<Guid, string> { [userId] = user.DisplayName }));
    }

    private sealed record Row(
        Guid Id, Guid AstronautId, string AstronautName, Guid CheckUpId, string Indicators,
        DateTimeOffset CreatedAt, DateTimeOffset? AcknowledgedAt, Guid? AcknowledgedByUserId, string? AcknowledgeNote);

    private static async Task<Dictionary<Guid, string>> NamesAsync(AppDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
        return wanted.Count == 0
            ? []
            : await db.PortalUsers.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private static AlertDto ToDto(Row r, IReadOnlyDictionary<Guid, string> names) => new(
        r.Id,
        r.AstronautId,
        r.AstronautName,
        r.CheckUpId,
        ParseIndicators(r.Indicators),
        r.CreatedAt,
        r.AcknowledgedAt,
        r.AcknowledgedByUserId is { } by ? names.GetValueOrDefault(by, "Unknown user") : null,
        r.AcknowledgeNote);

    // Stored as a comma-separated list of IndicatorType names (written by the sync endpoint).
    private static List<IndicatorType> ParseIndicators(string stored)
        => stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<IndicatorType>(s, ignoreCase: true, out var t) ? (IndicatorType?)t : null)
            .Where(t => t != null)
            .Select(t => t!.Value)
            .ToList();
}
