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

    private static async Task<IResult> GetAsync(
        AppDbContext db,
        CancellationToken ct,
        decimal hoursWithoutContact = 0m,
        decimal continuousStayHours = 0m)
    {
        if (ValidateHours(hoursWithoutContact, continuousStayHours) is { } invalid)
        {
            return invalid;
        }

        var mission = await db.Missions.AsNoTracking().OrderBy(m => m.Name).FirstOrDefaultAsync(ct);
        if (mission is null)
        {
            return Results.NotFound();
        }
        return Results.Ok(ToDto(mission, hoursWithoutContact, continuousStayHours));
    }

    internal static IResult? ValidateHours(decimal hoursWithoutContact, decimal continuousStayHours)
    {
        var errors = new Dictionary<string, string[]>();
        if (hoursWithoutContact < 0m)
        {
            errors["hoursWithoutContact"] = ["hoursWithoutContact must be 0 or greater."];
        }

        if (continuousStayHours < 0m)
        {
            errors["continuousStayHours"] = ["continuousStayHours must be 0 or greater."];
        }

        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    /// <summary>
    /// The mission profile with its isolation and confinement indices. The time inputs (hours without contact and
    /// continuous stay) are not stored per mission, so the apps pass what they know; 0 gives the profile-only index.
    /// A mission with no crew has no indices.
    /// </summary>
    internal static MissionProfileDto ToDto(Mission mission, decimal hoursWithoutContact = 0m, decimal continuousStayHours = 0m, DomainProfile? domain = null)
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
            (double)delay,
            profile.CrewSize > 0 ? IsolationConfinement.Evaluate(profile, hoursWithoutContact, continuousStayHours, domain) : null);
    }
}