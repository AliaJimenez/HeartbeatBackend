using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using System.Security.Claims;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Auth;

public static class AuthEndpoints
{
    private static readonly TimeSpan McRefreshLifetime = TimeSpan.FromHours(8);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var crew = app.MapGroup("/api/crew/v1/auth");
        crew.MapPost("/login", CrewLoginAsync);
        crew.MapPost("/refresh", CrewRefreshAsync);

        var mc = app.MapGroup("/api/mc/v1/auth");
        mc.MapPost("/login", McLoginAsync);
        mc.MapPost("/refresh", McRefreshAsync);

        // Any signed-in portal user (CONTRACTS.md §6); no specific permission needed.
        app.MapGroup("/api/mc/v1").MapGet("/me", McMeAsync).RequireAuthorization(AuthConstants.McPolicy);

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

        // Permissions always come from the role stored in the database (CONTRACTS.md §4).
        var dto = await LoadPortalUserAsync(db, user.Id, cancellationToken);
        if (dto is null || !dto.IsActive)
        {
            return Results.Problem(title: "Portal user not found or disabled", statusCode: StatusCodes.Status403Forbidden);
        }

        var accessToken = jwt.CreateAccessToken(user.Id, dto.DisplayName, AuthConstants.McAudience, permissions: dto.Permissions);
        var refreshToken = jwt.CreateRefreshToken(user.Id, AuthConstants.McAudience, McRefreshLifetime);

        return Results.Ok(new McLoginResponse(
            accessToken,
            DateTimeOffset.UtcNow.AddMinutes(60),
            refreshToken,
            DateTimeOffset.UtcNow.Add(McRefreshLifetime),
            dto));
    }

    private static async Task<IResult> McRefreshAsync(
        RefreshRequest request,
        AppDbContext db,
        JwtTokenService jwt,
        CancellationToken cancellationToken)
    {
        var principal = jwt.ValidateRefreshToken(request.RefreshToken, AuthConstants.McAudience);
        if (principal is null || !Guid.TryParse(principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value, out var userId))
        {
            return Results.Problem(title: "Invalid refresh token", statusCode: StatusCodes.Status401Unauthorized);
        }

        // Re-read the role so permission changes apply on refresh, and disabled users cannot keep a session.
        var dto = await LoadPortalUserAsync(db, userId, cancellationToken);
        if (dto is null || !dto.IsActive)
        {
            return Results.Problem(title: "Invalid refresh token", statusCode: StatusCodes.Status401Unauthorized);
        }

        var accessToken = jwt.CreateAccessToken(userId, dto.DisplayName, AuthConstants.McAudience, permissions: dto.Permissions);
        var refreshToken = jwt.CreateRefreshToken(userId, AuthConstants.McAudience, McRefreshLifetime);

        return Results.Ok(new TokenResponse(accessToken, DateTimeOffset.UtcNow.AddMinutes(60), refreshToken, DateTimeOffset.UtcNow.Add(McRefreshLifetime)));
    }

    private static async Task<IResult> McMeAsync(
        ClaimsPrincipal caller,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(caller.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value, out var userId))
        {
            return Results.Problem(title: "Invalid token", statusCode: StatusCodes.Status401Unauthorized);
        }

        var dto = await LoadPortalUserAsync(db, userId, cancellationToken);
        if (dto is null || !dto.IsActive)
        {
            return Results.Problem(title: "Portal user not found or disabled", statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(dto);
    }

    private static async Task<PortalUserDto?> LoadPortalUserAsync(AppDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var portalUser = await db.PortalUsers
            .AsNoTracking()
            .Include(p => p.Role)
            .ThenInclude(r => r.Permissions)
            .FirstOrDefaultAsync(p => p.Id == userId, cancellationToken);

        if (portalUser is null)
        {
            return null;
        }

        var permissions = portalUser.Role.Permissions
            .Select(rp => rp.Permission)
            .Where(p => Permissions.All.Contains(p))
            .Distinct()
            .Order()
            .ToList();

        var userCount = await db.PortalUsers.CountAsync(p => p.RoleId == portalUser.RoleId, cancellationToken);

        return new PortalUserDto(
            portalUser.Id,
            portalUser.UserName,
            portalUser.Email,
            portalUser.DisplayName,
            new RoleDto(portalUser.Role.Id, portalUser.Role.Name, portalUser.Role.IsSystem, userCount, permissions),
            portalUser.IsActive,
            permissions);
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