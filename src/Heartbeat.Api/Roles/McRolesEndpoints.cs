using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Roles;

/// <summary>Roles and the permission checklist (CONTRACTS.md §6, MC-ROLES / MR-15).</summary>
public static class McRolesEndpoints
{
    /// <summary>Policy for GET /roles: users.view or roles.manage.</summary>
    public const string ReadRolesPolicy = "roles:read";

    public static readonly IReadOnlyList<PermissionDto> Catalog =
    [
        new(Permissions.CrewView, "Crew", "See the crew list and profiles"),
        new(Permissions.CrewManage, "Crew", "Create and edit astronaut accounts, reset their passwords"),
        new(Permissions.VitalsView, "Vitals", "See latest status and check-up details"),
        new(Permissions.VitalsHistory, "Vitals", "See past check-ups and charts"),
        new(Permissions.AlertsView, "Alerts", "See critical alerts"),
        new(Permissions.AlertsAcknowledge, "Alerts", "Acknowledge an alert with a note"),
        new(Permissions.MessagesView, "Messages", "Read astronaut messages and reports"),
        new(Permissions.MessagesSend, "Messages", "Reply to an astronaut"),
        new(Permissions.UsersView, "Users", "See portal users"),
        new(Permissions.UsersManage, "Users", "Create, edit, disable users and assign roles"),
        new(Permissions.RolesManage, "Roles", "Create and edit roles and their permissions"),
    ];

    // An admin may not remove these from their own role, or nobody could fix the roles afterwards.
    private static readonly string[] SelfProtected = [Permissions.UsersManage, Permissions.RolesManage];

    public static AuthorizationOptions AddRolePolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(ReadRolesPolicy, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => ctx.User.HasClaim("aud", AuthConstants.McAudience)
                && (ctx.User.HasClaim(AuthConstants.PermissionClaim, Permissions.UsersView)
                    || ctx.User.HasClaim(AuthConstants.PermissionClaim, Permissions.RolesManage))));
        return options;
    }

    public static IEndpointRouteBuilder MapMcRolesEndpoints(this IEndpointRouteBuilder app)
    {
        var mc = app.MapGroup("/api/mc/v1");

        mc.MapGet("/roles", ListAsync).RequireAuthorization(ReadRolesPolicy);
        mc.MapPost("/roles", CreateAsync).RequirePermission(Permissions.RolesManage);
        mc.MapPut("/roles/{id:guid}", UpdateAsync).RequirePermission(Permissions.RolesManage);
        mc.MapDelete("/roles/{id:guid}", DeleteAsync).RequirePermission(Permissions.RolesManage);
        mc.MapGet("/permissions", () => Results.Ok(Catalog)).RequirePermission(Permissions.RolesManage);

        return app;
    }

    private static async Task<IResult> ListAsync(AppDbContext db, CancellationToken ct)
    {
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).ToListAsync(ct);
        var counts = await db.PortalUsers.AsNoTracking()
            .GroupBy(u => u.RoleId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return Results.Ok(roles
            .OrderByDescending(r => r.IsSystem).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => ToDto(r, counts.GetValueOrDefault(r.Id)))
            .ToList());
    }

    private static async Task<IResult> CreateAsync(SaveRoleRequest request, AppDbContext db, CancellationToken ct)
    {
        var (name, permissions, errors) = Validate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (await NameTakenAsync(db, name, exceptId: null, ct))
        {
            return Results.Problem(title: "A role with that name already exists", statusCode: StatusCodes.Status409Conflict);
        }

        var role = new Role { Id = Guid.CreateVersion7(), Name = name, IsSystem = false };
        foreach (var p in permissions)
        {
            role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = p });
        }

        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/mc/v1/roles/{role.Id}", ToDto(role, 0));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, SaveRoleRequest request, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct)
    {
        var (name, permissions, errors) = Validate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null)
        {
            return RoleNotFound();
        }

        if (role.IsSystem && role.Name == RoleNames.Administrator)
        {
            return Results.Problem(title: "The Administrator role cannot be edited", statusCode: StatusCodes.Status409Conflict);
        }

        if (await IsCallerRoleAsync(db, caller, id, ct))
        {
            var removed = SelfProtected.Where(p => !permissions.Contains(p)).ToList();
            if (removed.Count > 0)
            {
                return Results.Problem(
                    title: "You cannot remove permissions you need to manage roles and users from your own role",
                    detail: $"Missing: {string.Join(", ", removed)}",
                    statusCode: StatusCodes.Status409Conflict);
            }
        }

        if (await NameTakenAsync(db, name, exceptId: id, ct))
        {
            return Results.Problem(title: "A role with that name already exists", statusCode: StatusCodes.Status409Conflict);
        }

        role.Name = name;
        db.RolePermissions.RemoveRange(role.Permissions);
        role.Permissions = permissions.Select(p => new RolePermission { RoleId = id, Permission = p }).ToList();
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToDto(role, await db.PortalUsers.CountAsync(u => u.RoleId == id, ct)));
    }

    private static async Task<IResult> DeleteAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null)
        {
            return RoleNotFound();
        }

        if (role.IsSystem)
        {
            return Results.Problem(title: "System roles cannot be deleted", statusCode: StatusCodes.Status409Conflict);
        }

        var users = await db.PortalUsers.CountAsync(u => u.RoleId == id, ct);
        if (users > 0)
        {
            return Results.Problem(
                title: "The role still has users assigned",
                detail: $"{users} user(s) must be moved to another role first.",
                statusCode: StatusCodes.Status409Conflict);
        }

        db.RolePermissions.RemoveRange(role.Permissions);
        db.Roles.Remove(role);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static (string Name, List<string> Permissions, Dictionary<string, string[]> Errors) Validate(SaveRoleRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 50)
        {
            errors["name"] = ["Name is required and must be at most 50 characters."];
        }

        var requested = request.Permissions ?? [];
        var unknown = requested.Where(p => !Permissions.All.Contains(p)).Distinct().ToList();
        if (unknown.Count > 0)
        {
            errors["permissions"] = [$"Unknown permission(s): {string.Join(", ", unknown)}."];
        }

        return (name, requested.Where(Permissions.All.Contains).Distinct().ToList(), errors);
    }

    private static async Task<bool> NameTakenAsync(AppDbContext db, string name, Guid? exceptId, CancellationToken ct)
    {
        var lower = name.ToLowerInvariant();
        return await db.Roles.AnyAsync(r => r.Name.ToLower() == lower && r.Id != exceptId, ct);
    }

    private static async Task<bool> IsCallerRoleAsync(AppDbContext db, ClaimsPrincipal caller, Guid roleId, CancellationToken ct)
    {
        if (!Guid.TryParse(caller.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId))
        {
            return false;
        }

        return await db.PortalUsers.AnyAsync(u => u.Id == userId && u.RoleId == roleId, ct);
    }

    private static RoleDto ToDto(Role role, int userCount) => new(
        role.Id,
        role.Name,
        role.IsSystem,
        userCount,
        role.Permissions.Select(p => p.Permission).Where(Permissions.All.Contains).Order().ToList());

    private static IResult RoleNotFound() => Results.Problem(title: "Role not found", statusCode: StatusCodes.Status404NotFound);
}
