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