using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Heartbeat.Api.Crew;

public static class CrewEndpoints
{
    public static IEndpointRouteBuilder MapCrewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/crew/v1").RequireAuthorization("Crew");

        group.MapGet("/me", GetMeAsync);
        group.MapPut("/me", UpdateMeAsync);
        group.MapPost("/sync", SyncAsync);

        return app;
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
                });
            }

            db.CheckUps.Add(checkUp);

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

        if (lastPulled.HasValue)
        {
            inboundMessagesQuery = inboundMessagesQuery.Where(m => m.CreatedAt > lastPulled.Value || m.ReceivedAt == null);
        }

        var inboundMessagesEntities = await inboundMessagesQuery
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

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

        if (lastPulled.HasValue)
        {
            acknowledgedQuery = acknowledgedQuery.Where(a => a.AcknowledgedAt > lastPulled.Value);
        }

        var acknowledgedAlertsEntities = await acknowledgedQuery.ToListAsync(cancellationToken);
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