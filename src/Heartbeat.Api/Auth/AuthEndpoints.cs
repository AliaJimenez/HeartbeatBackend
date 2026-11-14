using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var crew = app.MapGroup("/api/crew/v1/auth");
        crew.MapPost("/login", CrewLoginAsync);
        crew.MapPost("/refresh", CrewRefreshAsync);

        var mc = app.MapGroup("/api/mc/v1/auth");
        mc.MapPost("/login", McLoginAsync);
        mc.MapPost("/refresh", McRefreshAsync);

        return app;
    }

    private static async Task<IResult> CrewLoginAsync(
        LoginRequest request,
        UserManager<ApplicationUser> userManager,
        AppDbContext db,
        JwtTokenService jwt,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByNameAsync(request.UserName);
        if (user is null || user.AccountType != AccountType.Astronaut)
        {
            return Results.Problem(title: "Invalid credentials", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            return Results.Problem(title: "Invalid credentials", statusCode: StatusCodes.Status401Unauthorized);
        }

        var astronaut = await db.Astronauts
            .Include(a => a.Mission)
            .FirstOrDefaultAsync(a => a.Id == user.Id, cancellationToken);

        if (astronaut is null)
        {
            return Results.Problem(title: "Astronaut profile not found", statusCode: StatusCodes.Status404NotFound);
        }

        var profile = ToProfile(astronaut);
        var accessToken = jwt.CreateAccessToken(user.Id, profile.DisplayName, "heartbeat-crew", profile.Mission.Id);
        var refreshToken = jwt.CreateRefreshToken(user.Id, "heartbeat-crew");

        return Results.Ok(new CrewLoginResponse(
            accessToken,
            DateTimeOffset.UtcNow.AddMinutes(60),
            refreshToken,
            DateTimeOffset.UtcNow.AddDays(14),
            profile));
    }

    private static async Task<IResult> CrewRefreshAsync(
        RefreshRequest request,
        JwtTokenService jwt,
        CancellationToken cancellationToken)
    {
        var principal = jwt.ValidateRefreshToken(request.RefreshToken, "heartbeat-crew");
        if (principal is null)
        {
            return Results.Problem(title: "Invalid refresh token", statusCode: StatusCodes.Status401Unauthorized);
        }

        var userId = Guid.Parse(principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value ?? throw new InvalidOperationException());
        var accessToken = jwt.CreateAccessToken(userId, principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Name)?.Value ?? string.Empty, "heartbeat-crew");
        var refreshToken = jwt.CreateRefreshToken(userId, "heartbeat-crew");

        return Results.Ok(new TokenResponse(accessToken, DateTimeOffset.UtcNow.AddMinutes(60), refreshToken, DateTimeOffset.UtcNow.AddDays(14)));
    }

    private static async Task<IResult> McLoginAsync(
        LoginRequest request,
        UserManager<ApplicationUser> userManager,
        AppDbContext db,
        JwtTokenService jwt,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByNameAsync(request.UserName);
        if (user is null || user.AccountType != AccountType.Portal)
        {
            return Results.Problem(title: "Invalid credentials", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            return Results.Problem(title: "Invalid credentials", statusCode: StatusCodes.Status401Unauthorized);
        }

        var portalUser = await db.PortalUsers
            .Include(p => p.Role)
            .ThenInclude(r => r.Permissions)
            .FirstOrDefaultAsync(p => p.Id == user.Id, cancellationToken);

        if (portalUser is null || !portalUser.IsActive)
        {
            return Results.Problem(title: "Portal user not found or disabled", statusCode: StatusCodes.Status403Forbidden);
        }

        var permissions = portalUser.Role.Permissions.Select(rp => rp.Permission).ToList();
        var dto = new PortalUserDto(
            portalUser.Id,
            portalUser.UserName,
            portalUser.Email,
            portalUser.DisplayName,
            new RoleDto(portalUser.Role.Id, portalUser.Role.Name, portalUser.Role.IsSystem, portalUser.Role.Users.Count, permissions),
            portalUser.IsActive,
            permissions);

        var accessToken = jwt.CreateAccessToken(user.Id, portalUser.DisplayName, "heartbeat-mc", permissions: permissions);
        var refreshToken = jwt.CreateRefreshToken(user.Id, "heartbeat-mc", TimeSpan.FromHours(8));

        return Results.Ok(new McLoginResponse(
            accessToken,
            DateTimeOffset.UtcNow.AddMinutes(60),
            refreshToken,
            DateTimeOffset.UtcNow.AddHours(8),
            dto));
    }

    private static async Task<IResult> McRefreshAsync(
        RefreshRequest request,
        JwtTokenService jwt,
        CancellationToken cancellationToken)
    {
        var principal = jwt.ValidateRefreshToken(request.RefreshToken, "heartbeat-mc");
        if (principal is null)
        {
            return Results.Problem(title: "Invalid refresh token", statusCode: StatusCodes.Status401Unauthorized);
        }

        var userId = Guid.Parse(principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value ?? throw new InvalidOperationException());
        var accessToken = jwt.CreateAccessToken(userId, principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Name)?.Value ?? string.Empty, "heartbeat-mc");
        var refreshToken = jwt.CreateRefreshToken(userId, "heartbeat-mc", TimeSpan.FromHours(8));

        return Results.Ok(new TokenResponse(accessToken, DateTimeOffset.UtcNow.AddMinutes(60), refreshToken, DateTimeOffset.UtcNow.AddHours(8)));
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