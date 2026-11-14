using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using System.Security.Claims;

namespace Heartbeat.Api.Crew;

public static class CrewEndpoints
{
    public static IEndpointRouteBuilder MapCrewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/crew/v1").RequireAuthorization("Crew");

        group.MapGet("/me", GetMeAsync);
        group.MapPut("/me", UpdateMeAsync);
        group.MapGet("/reference-ranges", GetReferenceRanges);
        group.MapPost("/sync", SyncAsync);
        group.MapGet("/checkups", GetCheckUpsAsync);

        return app;
    }

    private static IResult GetReferenceRanges(HttpContext context)
    {
        var etag = new EntityTagHeaderValue($"\"{ReferenceRanges.Version}\"");
        var headers = context.Response.GetTypedHeaders();
        headers.ETag = etag;
        headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        var ifNoneMatch = context.Request.GetTypedHeaders().IfNoneMatch;
        var matches = ifNoneMatch.Any(t => t == EntityTagHeaderValue.Any || t.Compare(etag, useStrongComparison: false));
        return matches
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.Content(ReferenceRanges.Json, "application/json");
    }

    private static async Task<IResult> GetMeAsync(
        ClaimsPrincipal principal,
        UserManager<ApplicationUser> userManager,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Problem(title: "Invalid token", statusCode: StatusCodes.Status401Unauthorized);
        }

        var user = await userManager.FindByIdAsync(userId.Value.ToString());
        if (user is null || user.AccountType != AccountType.Astronaut)
        {
            return Results.Problem(title: "Astronaut account not found", statusCode: StatusCodes.Status404NotFound);
        }

        var astronaut = await db.Astronauts
            .Include(a => a.Mission)
            .FirstOrDefaultAsync(a => a.Id == userId.Value, cancellationToken);

        if (astronaut is null)
        {
            return Results.Problem(title: "Astronaut profile not found", statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(ToProfile(astronaut));
    }

    private static async Task<IResult> UpdateMeAsync(
        UpdateProfileRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Problem(title: "Invalid token", statusCode: StatusCodes.Status401Unauthorized);
        }

        var astronaut = await db.Astronauts
            .Include(a => a.Mission)
            .FirstOrDefaultAsync(a => a.Id == userId.Value, cancellationToken);

        if (astronaut is null)
        {
            return Results.Problem(title: "Astronaut profile not found", statusCode: StatusCodes.Status404NotFound);
        }

        if (request.UpdatedAt >= astronaut.UpdatedAt)
        {
            astronaut.DisplayName = request.DisplayName;
            astronaut.CallSign = request.CallSign;
            astronaut.Notes = request.Notes;
            astronaut.UpdatedAt = request.UpdatedAt;
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(ToProfile(astronaut));
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private static async Task<IResult> SyncAsync(
        SyncRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Problem(title: "Invalid token", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (request.CheckUps.Count > 500)
        {
            return Results.Problem(title: "At most 500 check-ups per sync", statusCode: StatusCodes.Status400BadRequest);
        }

        var astronaut = await db.Astronauts
            .Include(a => a.Mission)
            .FirstOrDefaultAsync(a => a.Id == userId.Value, cancellationToken);

        if (astronaut is null)
        {
            return Results.Problem(title: "Astronaut profile not found", statusCode: StatusCodes.Status404NotFound);
        }

        var evaluator = new HealthEvaluator();
        var accepted = new List<Guid>();
        var rejected = new List<SyncRejected>();
        var seen = new HashSet<Guid>();
        var serverNow = DateTimeOffset.UtcNow;

        foreach (var checkUpDto in request.CheckUps)
        {
            if (!seen.Add(checkUpDto.Id))
            {
                accepted.Add(checkUpDto.Id);
                continue;
            }

            var existing = await db.CheckUps.AnyAsync(c => c.Id == checkUpDto.Id, cancellationToken);
            if (existing)
            {
                accepted.Add(checkUpDto.Id);
                continue;
            }

            var inputs = checkUpDto.Readings
                .Select(r => new ReadingInput(r.Indicator, r.Value, r.SecondaryValue))
                .ToList();

            var errors = new List<string>();
            foreach (var input in inputs)
            {
                errors.AddRange(evaluator.Validate(input));
            }

            if (checkUpDto.Readings.Any(r => r.DeviceId is { Length: > Reading.DeviceIdMaxLength }))
            {
                errors.Add($"Reading deviceId is longer than {Reading.DeviceIdMaxLength} characters");
            }

            if (errors.Count > 0)
            {
                rejected.Add(new SyncRejected(checkUpDto.Id, errors[0]));
                continue;
            }

            var evaluation = evaluator.Evaluate(inputs);

            var checkUp = new CheckUp
            {
                Id = checkUpDto.Id,
                AstronautId = userId.Value,
                RecordedAt = checkUpDto.RecordedAt,
                ReceivedAt = serverNow,
                OverallStatus = evaluation.OverallStatus,
                RangesVersion = evaluator.RangesVersion,
                Note = checkUpDto.Note,
                SupersedesId = checkUpDto.SupersedesId,
            };

            for (var i = 0; i < inputs.Count; i++)
            {
                var readingEval = evaluation.Readings[i];
                var readingDto = i < checkUpDto.Readings.Count ? checkUpDto.Readings[i] : null;
                checkUp.Readings.Add(new Reading
                {
                    Id = readingDto?.Id ?? Guid.NewGuid(),
                    Indicator = inputs[i].Indicator,
                    Value = inputs[i].Value,
                    SecondaryValue = inputs[i].SecondaryValue,
                    Status = readingEval.Status,
                    BandIds = string.Join(",", readingEval.BandIds),
                    Source = readingDto?.Source ?? ReadingSource.Manual,
                    DeviceId = string.IsNullOrWhiteSpace(readingDto?.DeviceId) ? null : readingDto.DeviceId.Trim(),
                });
            }

            db.CheckUps.Add(checkUp);

            if (evaluation.OverallStatus == HealthStatus.Critical)
            {
                var alreadyAlert = await db.Alerts.AnyAsync(a => a.CheckUpId == checkUp.Id, cancellationToken);
                if (!alreadyAlert)
                {
                    var criticalIndicators = evaluation.Readings
                        .Where(r => r.Status == HealthStatus.Critical)
                        .Select(r => r.Indicator.ToString())
                        .Distinct()
                        .ToList();

                    db.Alerts.Add(new Alert
                    {
                        Id = Guid.NewGuid(),
                        AstronautId = userId.Value,
                        CheckUpId = checkUp.Id,
                        Indicators = string.Join(",", criticalIndicators),
                        CreatedAt = serverNow,
                    });
                }
            }

            accepted.Add(checkUpDto.Id);
        }

        AstronautProfile? responseProfile = null;
        if (request.Profile is not null && request.Profile.UpdatedAt > astronaut.UpdatedAt)
        {
            astronaut.DisplayName = request.Profile.DisplayName;
            astronaut.CallSign = request.Profile.CallSign;
            astronaut.Notes = request.Profile.Notes;
            astronaut.UpdatedAt = request.Profile.UpdatedAt;
            responseProfile = ToProfile(astronaut);
        }

        var lastPulled = request.LastPulledAt;
        var inboundMessagesQuery = db.Messages
            .Where(m => m.AstronautId == userId.Value && m.Direction == MessageDirection.ToAstronaut);

        // SQLite cannot compare or ORDER BY a DateTimeOffset, so filter and order in memory.
        var inboundMessagesEntities = (await inboundMessagesQuery.ToListAsync(cancellationToken))
            .Where(m => !lastPulled.HasValue || m.CreatedAt > lastPulled.Value || m.ReceivedAt == null)
            .OrderBy(m => m.CreatedAt)
            .ToList();

        foreach (var message in inboundMessagesEntities)
        {
            if (message.ReceivedAt is null)
            {
                message.ReceivedAt = serverNow;
            }
        }

        var inboundMessages = inboundMessagesEntities.Select(m => new MessageDto(
            m.Id,
            m.Direction,
            m.Kind,
            m.CheckUpId,
            m.Subject,
            m.Body,
            m.CreatedAt,
            m.ReceivedAt,
            m.ReadAt,
            null)).ToList();

        var acknowledgedQuery = db.Alerts
            .Where(a => a.AstronautId == userId.Value && a.AcknowledgedAt != null);

        var acknowledgedAlertsEntities = (await acknowledgedQuery.ToListAsync(cancellationToken))
            .Where(a => !lastPulled.HasValue || a.AcknowledgedAt > lastPulled.Value)
            .ToList();
        var acknowledgedAlerts = acknowledgedAlertsEntities.Select(a => new AcknowledgedAlert(
            a.CheckUpId,
            a.AcknowledgedAt!.Value,
            "Mission Control",
            a.AcknowledgeNote)).ToList();

        await db.SaveChangesAsync(cancellationToken);

        var rangesUpdateAvailable = request.RangesVersion != evaluator.RangesVersion;

        return Results.Ok(new SyncResponse(
            serverNow,
            accepted,
            rejected,
            responseProfile,
            new SyncInbound(inboundMessages, acknowledgedAlerts),
            rangesUpdateAvailable));
    }

    private static async Task<IResult> GetCheckUpsAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Problem(title: "Invalid token", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > 200) pageSize = 200;

        var supersededIds = await db.CheckUps
            .Where(c => c.AstronautId == userId.Value && c.SupersedesId != null)
            .Select(c => c.SupersedesId!.Value)
            .ToListAsync(cancellationToken);

        var query = db.CheckUps
            .AsNoTracking()
            .Where(c => c.AstronautId == userId.Value && !supersededIds.Contains(c.Id));

        // SQLite cannot compare or ORDER BY a DateTimeOffset: filter, order and page the (small) id list in memory,
        // then load only that page.
        var ordered = (await query.Select(c => new { c.Id, c.RecordedAt }).ToListAsync(cancellationToken))
            .Where(c => (!from.HasValue || c.RecordedAt >= from.Value) && (!to.HasValue || c.RecordedAt <= to.Value))
            .OrderByDescending(c => c.RecordedAt)
            .ToList();
        var total = ordered.Count;
        var pageIds = ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(c => c.Id).ToList();

        var loaded = await db.CheckUps
            .AsNoTracking()
            .Include(c => c.Readings)
            .Where(c => pageIds.Contains(c.Id))
            .ToListAsync(cancellationToken);
        var items = pageIds.Select(id => loaded.Single(c => c.Id == id)).ToList();

        var dtoItems = items.Select(ToCheckUpDto).ToList();

        return Results.Ok(new Paged<CheckUpDto>(dtoItems, page, pageSize, total));
    }

    private static CheckUpDto ToCheckUpDto(CheckUp checkUp)
    {
        return new CheckUpDto(
            checkUp.Id,
            checkUp.RecordedAt,
            checkUp.ReceivedAt,
            checkUp.OverallStatus,
            checkUp.RangesVersion,
            checkUp.Note,
            checkUp.SupersedesId,
            checkUp.Readings.Select(r => new ReadingDto(
                r.Id,
                r.Indicator,
                r.Value,
                r.SecondaryValue,
                r.Status,
                r.BandIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                r.Source,
                r.DeviceId)).ToList());
    }

    private static AstronautProfile ToProfile(Astronaut astronaut)
    {
        return new AstronautProfile(
            astronaut.Id,
            astronaut.UserName,
            astronaut.DisplayName,
            astronaut.CallSign,
            new MissionInfo(astronaut.Mission.Id, astronaut.Mission.Name, astronaut.Mission.Environment, astronaut.Mission.StartsAt),
            astronaut.DateOfBirth,
            astronaut.Notes,
            astronaut.UpdatedAt);
    }
}