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
        group.MapGet("/radiation", GetRadiationAsync);
        group.MapGet("/mission", GetMissionAsync);

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

        // Messages and reports pushed by the astronaut (MR-11). Idempotent by id, like check-ups.
        var checkUpIdsOfThisSync = accepted.ToHashSet();
        var seenMessages = new HashSet<Guid>();
        foreach (var dto in request.Messages ?? [])
        {
            if (!seenMessages.Add(dto.Id))
            {
                accepted.Add(dto.Id);
                continue;
            }

            var stored = await db.Messages.AsNoTracking()
                .Where(m => m.Id == dto.Id)
                .Select(m => new { m.AstronautId })
                .FirstOrDefaultAsync(cancellationToken);
            if (stored is not null)
            {
                // Already stored: a retry is accepted, but another astronaut's id is never reused.
                if (stored.AstronautId == userId.Value)
                {
                    accepted.Add(dto.Id);
                }
                else
                {
                    rejected.Add(new SyncRejected(dto.Id, "Message id is already in use"));
                }

                continue;
            }

            var problem = await ValidateIncomingMessageAsync(dto, userId.Value, checkUpIdsOfThisSync, db, cancellationToken);
            if (problem is not null)
            {
                rejected.Add(new SyncRejected(dto.Id, problem));
                continue;
            }

            db.Messages.Add(new Message
            {
                Id = dto.Id,
                AstronautId = userId.Value,            // always from the token, never from the body
                Direction = MessageDirection.ToMissionControl,
                Kind = dto.Kind,
                CheckUpId = dto.CheckUpId,
                Subject = dto.Subject.Trim(),
                Body = dto.Body.Trim(),
                CreatedAt = dto.CreatedAt,             // sender's clock
                ReceivedAt = serverNow,
            });
            accepted.Add(dto.Id);
        }

        var radiationTotal = await SyncRadiationDosesAsync(
            db, userId.Value, request.RadiationDoses ?? [], serverNow, accepted, rejected, cancellationToken);

        await EnvironmentReadings.SyncAsync(
            db, userId.Value, astronaut.MissionId, request.EnvironmentReadings ?? [], serverNow, accepted, rejected, cancellationToken);

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

        var senderIds = inboundMessagesEntities.Where(m => m.SenderUserId != null).Select(m => m.SenderUserId!.Value).Distinct().ToList();
        var senderNames = senderIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.PortalUsers.AsNoTracking().Where(u => senderIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);

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
            m.SenderUserId is { } by ? senderNames.GetValueOrDefault(by, "Mission Control") : "Mission Control")).ToList();

        var acknowledgedQuery = db.Alerts
            .Where(a => a.AstronautId == userId.Value && a.AcknowledgedAt != null);

        var acknowledgedAlertsEntities = (await acknowledgedQuery.ToListAsync(cancellationToken))
            .Where(a => !lastPulled.HasValue || a.AcknowledgedAt > lastPulled.Value)
            .ToList();
        var ackUserIds = acknowledgedAlertsEntities.Where(a => a.AcknowledgedByUserId != null).Select(a => a.AcknowledgedByUserId!.Value).Distinct().ToList();
        var ackNames = ackUserIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.PortalUsers.AsNoTracking().Where(u => ackUserIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
        var acknowledgedAlerts = acknowledgedAlertsEntities.Select(a => new AcknowledgedAlert(
            a.CheckUpId,
            a.AcknowledgedAt!.Value,
            a.AcknowledgedByUserId is { } by ? ackNames.GetValueOrDefault(by, "Mission Control") : "Mission Control",
            a.AcknowledgeNote,
            a.RadiationDoseId)).ToList();

        await db.SaveChangesAsync(cancellationToken);

        var rangesUpdateAvailable = request.RangesVersion != evaluator.RangesVersion;

        return Results.Ok(new SyncResponse(
            serverNow,
            accepted,
            rejected,
            responseProfile,
            new SyncInbound(inboundMessages, acknowledgedAlerts),
            rangesUpdateAvailable,
            radiationTotal));
    }

    // Radiation doses sync like check-ups: created on the desktop with their own id, so a retried sync is harmless.
    // Returns the astronaut's cumulative dose after this batch (summed in memory: SQLite cannot SUM a decimal).
    private static async Task<decimal> SyncRadiationDosesAsync(
        AppDbContext db,
        Guid astronautId,
        IReadOnlyList<RadiationDoseDto> doses,
        DateTimeOffset serverNow,
        List<Guid> accepted,
        List<SyncRejected> rejected,
        CancellationToken cancellationToken)
    {
        var stored = await db.RadiationDoses
            .AsNoTracking()
            .Where(d => d.AstronautId == astronautId)
            .Select(d => new { d.Id, d.DoseMsv })
            .ToListAsync(cancellationToken);
        var total = RadiationDoses.Total(stored.Select(d => d.DoseMsv));

        if (doses.Count == 0)
        {
            return total;
        }

        var mine = stored.Select(d => d.Id).ToHashSet();
        var incomingIds = doses.Select(d => d.Id).ToList();
        var ownedByOthers = (await db.RadiationDoses
                .AsNoTracking()
                .Where(d => incomingIds.Contains(d.Id) && d.AstronautId != astronautId)
                .Select(d => d.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var dose in doses)
        {
            if (ownedByOthers.Contains(dose.Id))
            {
                rejected.Add(new SyncRejected(dose.Id, "Radiation dose id is already in use"));
                continue;
            }

            if (mine.Contains(dose.Id))
            {
                accepted.Add(dose.Id);
                continue;
            }

            var note = string.IsNullOrWhiteSpace(dose.Note) ? null : dose.Note.Trim();
            var errors = RadiationDoses.Validate(dose.DoseMsv, note);
            if (errors.Count > 0)
            {
                rejected.Add(new SyncRejected(dose.Id, errors[0]));
                continue;
            }

            db.RadiationDoses.Add(new RadiationDose
            {
                Id = dose.Id,
                AstronautId = astronautId,
                RecordedAt = dose.RecordedAt,
                ReceivedAt = serverNow,
                DoseMsv = dose.DoseMsv,
                Note = note,
                Type = dose.Type,
            });
            mine.Add(dose.Id);
            var totalBefore = total;
            total += dose.DoseMsv;
            accepted.Add(dose.Id);

            await RaiseRadiationAlertsAsync(db, astronautId, dose, totalBefore, total, serverNow, cancellationToken);
        }

        return total;
    }

    // Radiation alerts (HB-BE-02): a solar particle event, or the cumulative dose reaching the critical band, creates an
    // Alert row so it shows up in the portal without anyone asking. Both are created once, so a retried sync is harmless.
    private static async Task RaiseRadiationAlertsAsync(
        AppDbContext db,
        Guid astronautId,
        RadiationDoseDto dose,
        decimal totalBefore,
        decimal totalAfter,
        DateTimeOffset serverNow,
        CancellationToken cancellationToken)
    {
        if (dose.Type == RadiationType.Spe)
        {
            db.Alerts.Add(new Alert
            {
                Id = Guid.CreateVersion7(),
                AstronautId = astronautId,
                Kind = AlertKind.Radiation,
                RadiationDoseId = dose.Id,
                Indicators = RadiationDoses.AlertSpe,
                Message = RadiationDoses.SpeAlertMessage(dose.DoseMsv),
                CreatedAt = serverNow,
            });
        }

        var wasCritical = RadiationDoses.StatusForCareerPercent(RadiationDoses.PercentOfCareerLimit(totalBefore)) == HealthStatus.Critical;
        var isCritical = RadiationDoses.StatusForCareerPercent(RadiationDoses.PercentOfCareerLimit(totalAfter)) == HealthStatus.Critical;
        if (isCritical && !wasCritical)
        {
            var alreadyRaised = await db.Alerts.AnyAsync(
                a => a.AstronautId == astronautId && a.Indicators == RadiationDoses.AlertCumulative, cancellationToken);
            if (!alreadyRaised)
            {
                db.Alerts.Add(new Alert
                {
                    Id = Guid.CreateVersion7(),
                    AstronautId = astronautId,
                    Kind = AlertKind.Radiation,
                    RadiationDoseId = dose.Id,
                    Indicators = RadiationDoses.AlertCumulative,
                    Message = RadiationDoses.CumulativeAlertMessage(totalAfter),
                    CreatedAt = serverNow,
                });
            }
        }
    }

    private static async Task<IResult> GetMissionAsync(
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

        var astronaut = await db.Astronauts
            .Include(a => a.Mission)
            .FirstOrDefaultAsync(a => a.Id == userId.Value, cancellationToken);

        if (astronaut is null)
        {
            return Results.Problem(title: "Astronaut profile not found", statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(McMissionEndpoints.ToDto(astronaut.Mission));
    }

    private static async Task<IResult> GetRadiationAsync(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Problem(title: "Invalid token", statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(await RadiationSummaries.LoadAsync(db, userId.Value, cancellationToken));
    }

    // Returns why a pushed message must be rejected, or null when it can be stored.
    private static async Task<string?> ValidateIncomingMessageAsync(
        MessageDto dto, Guid astronautId, HashSet<Guid> checkUpIdsOfThisSync, AppDbContext db, CancellationToken ct)
    {
        if (dto.Direction != MessageDirection.ToMissionControl)
        {
            return "Only toMissionControl messages can be pushed";
        }

        if (string.IsNullOrWhiteSpace(dto.Subject) || dto.Subject.Trim().Length > Heartbeat.Api.Messages.McMessagesEndpoints.MaxSubjectLength)
        {
            return $"Subject is required and must be at most {Heartbeat.Api.Messages.McMessagesEndpoints.MaxSubjectLength} characters";
        }

        if (string.IsNullOrWhiteSpace(dto.Body) || dto.Body.Trim().Length > Heartbeat.Api.Messages.McMessagesEndpoints.MaxBodyLength)
        {
            return $"Body is required and must be at most {Heartbeat.Api.Messages.McMessagesEndpoints.MaxBodyLength} characters";
        }

        if (dto.Kind == MessageKind.Report && dto.CheckUpId is null)
        {
            return "A report must reference its check-up";
        }

        if (dto.CheckUpId is { } checkUpId
            && !checkUpIdsOfThisSync.Contains(checkUpId)
            && !await db.CheckUps.AnyAsync(c => c.Id == checkUpId && c.AstronautId == astronautId, ct))
        {
            return "The referenced check-up does not exist or is not yours";
        }

        return null;
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
            new MissionInfo(astronaut.Mission.Id, astronaut.Mission.Name, astronaut.Mission.Environment, astronaut.Mission.StartsAt,
                new EnvironmentProfile(astronaut.Mission.EnvironmentType ?? astronaut.Mission.Environment, astronaut.Mission.GravityG, astronaut.Mission.DistanceKm, astronaut.Mission.CrewSize, astronaut.Mission.HabitableVolumeM3)),
            astronaut.DateOfBirth,
            astronaut.Notes,
            astronaut.UpdatedAt);
    }
}