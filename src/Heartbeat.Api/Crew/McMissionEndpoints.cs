using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Crew;

/// <summary>Mission environment profile for Mission Control (HB-BE-04).</summary>
public static class McMissionEndpoints
{
    public static IEndpointRouteBuilder MapMcMissionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mc/v1").RequireAuthorization("Mc");
        group.MapGet("/mission", GetAsync).RequirePermission(Permissions.CrewView);
        return app;
    }

    private static async Task<IResult> GetAsync(AppDbContext db, CancellationToken ct)
    {
        var mission = await db.Missions.AsNoTracking().OrderBy(m => m.Name).FirstOrDefaultAsync(ct);
        if (mission is null)
        {
            return Results.NotFound();
        }
        return Results.Ok(ToDto(mission));
    }

    internal static MissionProfileDto ToDto(Mission mission)
    {
        var profile = new EnvironmentProfile(
            string.IsNullOrWhiteSpace(mission.EnvironmentType) ? mission.Environment : mission.EnvironmentType,
            mission.GravityG,
            mission.DistanceKm,
            mission.CrewSize,
            mission.HabitableVolumeM3);

        var delay = profile.DistanceKm / 299792.458m;
        return new MissionProfileDto(
            mission.Id,
            mission.Name,
            mission.Environment,
            profile,
            (double)delay);
    }
}