using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Crew;

/// <summary>Mission Control crew list and astronaut account management (CONTRACTS.md §6, MC-CREW).</summary>
public static class McCrewEndpoints
{
    public static IEndpointRouteBuilder MapMcCrewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mc/v1/crew");

        group.MapGet("/", ListAsync).RequirePermission(Permissions.CrewView);
        group.MapGet("/{id:guid}", GetAsync).RequirePermission(Permissions.CrewView);
        group.MapPost("/", CreateAsync).RequirePermission(Permissions.CrewManage);
        group.MapPut("/{id:guid}", UpdateAsync).RequirePermission(Permissions.CrewManage);
        group.MapPost("/{id:guid}/reset-password", ResetPasswordAsync).RequirePermission(Permissions.CrewManage);

        return app;
    }

    public static async Task<IReadOnlyList<CrewSummaryDto>> BuildSummariesAsync(AppDbContext db, CancellationToken ct)
    {
        var astronauts = await db.Astronauts.AsNoTracking()
            .Select(a => new { a.Id, a.DisplayName, a.CallSign })
            .ToListAsync(ct);

        // SQLite cannot ORDER BY DateTimeOffset, so the latest check-up is picked in memory from a thin projection.
        var checkUps = await db.CheckUps.AsNoTracking()
            .Select(c => new { c.Id, c.AstronautId, c.RecordedAt, c.ReceivedAt, c.OverallStatus, c.SupersedesId })
            .ToListAsync(ct);

        var superseded = checkUps.Where(c => c.SupersedesId != null).Select(c => c.SupersedesId!.Value).ToHashSet();
        var byAstronaut = checkUps.GroupBy(c => c.AstronautId).ToDictionary(g => g.Key, g => g.ToList());

        var openAlerts = await db.Alerts.AsNoTracking()
            .Where(a => a.AcknowledgedAt == null)
            .GroupBy(a => a.AstronautId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var unread = await db.Messages.AsNoTracking()
            .Where(m => m.Direction == MessageDirection.ToMissionControl && m.ReadAt == null)
            .GroupBy(m => m.AstronautId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return astronauts
            .Select(a =>
            {
                var mine = byAstronaut.GetValueOrDefault(a.Id) ?? [];
                var latest = mine.Where(c => !superseded.Contains(c.Id)).OrderByDescending(c => c.RecordedAt).FirstOrDefault();
                var lastSync = mine.Where(c => c.ReceivedAt != null).Select(c => c.ReceivedAt).Max();

                return new CrewSummaryDto(
                    a.Id,
                    a.DisplayName,
                    a.CallSign,
                    latest?.OverallStatus,
                    latest?.RecordedAt,
                    lastSync,
                    openAlerts.GetValueOrDefault(a.Id),
                    unread.GetValueOrDefault(a.Id));
            })
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IResult> ListAsync(AppDbContext db, CancellationToken ct)
        => Results.Ok(await BuildSummariesAsync(db, ct));

    private static async Task<IResult> GetAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var astronaut = await db.Astronauts.AsNoTracking().Include(a => a.Mission).FirstOrDefaultAsync(a => a.Id == id, ct);
        return astronaut is null ? NotFound() : Results.Ok(ToProfile(astronaut));
    }

    private static async Task<IResult> CreateAsync(
        CreateAstronautRequest request,
        UserManager<ApplicationUser> userManager,
        AppDbContext db,
        CancellationToken ct)
    {
        var errors = ValidateCommon(request.DisplayName, request.CallSign, request.Notes);
        var userName = request.UserName?.Trim() ?? string.Empty;
        if (userName.Length is 0 or > 50)
        {
            errors["userName"] = ["UserName is required and must be at most 50 characters."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var mission = await db.Missions.FirstOrDefaultAsync(m => m.Id == request.MissionId, ct);
        if (mission is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["missionId"] = ["Mission does not exist."] });
        }

        if (await userManager.FindByNameAsync(userName) is not null)
        {
            return Results.Problem(title: "UserName already in use", statusCode: StatusCodes.Status409Conflict);
        }

        var id = Guid.CreateVersion7();
        var user = new ApplicationUser
        {
            Id = id,
            UserName = userName,
            DisplayName = request.DisplayName.Trim(),
            AccountType = AccountType.Astronaut,
        };

        // The Identity row and the Astronaut row must succeed or fail together.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var created = await userManager.CreateAsync(user, request.Password ?? string.Empty);
        if (!created.Succeeded)
        {
            return Results.ValidationProblem(IdentityErrors(created, "password"));
        }

        var astronaut = new Astronaut
        {
            Id = id,
            UserName = userName,
            DisplayName = user.DisplayName,
            CallSign = Blank(request.CallSign),
            MissionId = mission.Id,
            DateOfBirth = request.DateOfBirth,
            Notes = Blank(request.Notes),
            UpdatedAt = DateTimeOffset.UtcNow,
            Mission = mission,
        };
        db.Astronauts.Add(astronaut);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.Created($"/api/mc/v1/crew/{id}", ToProfile(astronaut));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateAstronautRequest request,
        UserManager<ApplicationUser> userManager,
        AppDbContext db,
        CancellationToken ct)
    {
        var errors = ValidateCommon(request.DisplayName, request.CallSign, request.Notes);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var astronaut = await db.Astronauts.Include(a => a.Mission).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (astronaut is null)
        {
            return NotFound();
        }

        if (astronaut.MissionId != request.MissionId)
        {
            var mission = await db.Missions.FirstOrDefaultAsync(m => m.Id == request.MissionId, ct);
            if (mission is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["missionId"] = ["Mission does not exist."] });
            }

            astronaut.Mission = mission;
            astronaut.MissionId = mission.Id;
        }

        astronaut.DisplayName = request.DisplayName.Trim();
        astronaut.CallSign = Blank(request.CallSign);
        astronaut.DateOfBirth = request.DateOfBirth;
        astronaut.Notes = Blank(request.Notes);
        astronaut.UpdatedAt = DateTimeOffset.UtcNow; // profile sync is last-write-wins on this value

        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is not null)
        {
            user.DisplayName = astronaut.DisplayName;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToProfile(astronaut));
    }

    private static async Task<IResult> ResetPasswordAsync(
        Guid id,
        ResetPasswordRequest request,
        UserManager<ApplicationUser> userManager,
        AppDbContext db,
        CancellationToken ct)
    {
        if (!await db.Astronauts.AnyAsync(a => a.Id == id, ct))
        {
            return NotFound();
        }

        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null || user.AccountType != AccountType.Astronaut)
        {
            return NotFound();
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, request.NewPassword ?? string.Empty);
        return result.Succeeded
            ? Results.NoContent()
            : Results.ValidationProblem(IdentityErrors(result, "newPassword"));
    }

    private static Dictionary<string, string[]> ValidateCommon(string? displayName, string? callSign, string? notes)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 100)
        {
            errors["displayName"] = ["DisplayName is required and must be at most 100 characters."];
        }

        if (callSign is { Length: > 30 })
        {
            errors["callSign"] = ["CallSign must be at most 30 characters."];
        }

        if (notes is { Length: > 500 })
        {
            errors["notes"] = ["Notes must be at most 500 characters."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> IdentityErrors(IdentityResult result, string field)
        => new() { [field] = result.Errors.Select(e => e.Description).ToArray() };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult NotFound() => Results.Problem(title: "Astronaut not found", statusCode: StatusCodes.Status404NotFound);

    private static AstronautProfile ToProfile(Astronaut a) => new(
        a.Id,
        a.UserName,
        a.DisplayName,
        a.CallSign,
        new MissionInfo(a.Mission.Id, a.Mission.Name, a.Mission.Environment, a.Mission.StartsAt,
            new EnvironmentProfile(a.Mission.EnvironmentType ?? a.Mission.Environment, a.Mission.GravityG, a.Mission.DistanceKm, a.Mission.CrewSize, a.Mission.HabitableVolumeM3)),
        a.DateOfBirth,
        a.Notes,
        a.UpdatedAt);
}
