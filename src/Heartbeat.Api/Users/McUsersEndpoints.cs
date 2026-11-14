using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Users;

/// <summary>Portal user management (CONTRACTS.md §6, MC-USERS / MR-16).</summary>
/// <remarks>
/// A role change takes effect on the user's next token refresh or login, because permissions are read
/// from the database when a token is issued (CONTRACTS.md §4) — nothing here touches existing tokens.
/// </remarks>
public static class McUsersEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;
    private static readonly string[] SelfProtected = [Permissions.UsersManage, Permissions.RolesManage];

    public static IEndpointRouteBuilder MapMcUsersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mc/v1/users");

        group.MapGet("/", ListAsync).RequirePermission(Permissions.UsersView);
        group.MapPost("/", CreateAsync).RequirePermission(Permissions.UsersManage);
        group.MapPut("/{id:guid}", UpdateAsync).RequirePermission(Permissions.UsersManage);
        group.MapPut("/{id:guid}/role", SetRoleAsync).RequirePermission(Permissions.UsersManage);
        group.MapPost("/{id:guid}/disable", DisableAsync).RequirePermission(Permissions.UsersManage);
        group.MapPost("/{id:guid}/enable", EnableAsync).RequirePermission(Permissions.UsersManage);

        return app;
    }

    private static async Task<IResult> ListAsync(AppDbContext db, CancellationToken ct, int page = 1, int pageSize = DefaultPageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
        {
            errors["page"] = ["page must be 1 or greater."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var total = await db.PortalUsers.CountAsync(ct);
        var users = await db.PortalUsers.AsNoTracking()
            .Include(u => u.Role).ThenInclude(r => r.Permissions)
            .OrderBy(u => u.DisplayName).ThenBy(u => u.UserName)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        var counts = await RoleCountsAsync(db, ct);
        return Results.Ok(new Paged<PortalUserDto>(users.Select(u => ToDto(u, counts)).ToList(), page, pageSize, total));
    }

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request, UserManager<ApplicationUser> userManager, AppDbContext db, CancellationToken ct)
    {
        var userName = request.UserName?.Trim() ?? string.Empty;
        var email = request.Email?.Trim() ?? string.Empty;
        var displayName = request.DisplayName?.Trim() ?? string.Empty;

        var errors = Validate(email, displayName);
        if (userName.Length is 0 or > 50)
        {
            errors["userName"] = ["UserName is required and must be at most 50 characters."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == request.RoleId, ct);
        if (role is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["roleId"] = ["Role does not exist."] });
        }

        if (await userManager.FindByNameAsync(userName) is not null)
        {
            return Conflict("UserName already in use");
        }

        if (await EmailTakenAsync(db, email, exceptId: null, ct))
        {
            return Conflict("Email already in use");
        }

        var id = Guid.CreateVersion7();
        var identity = new ApplicationUser
        {
            Id = id,
            UserName = userName,
            Email = email,
            DisplayName = displayName,
            AccountType = AccountType.Portal,
        };

        // The Identity row and the PortalUser row must succeed or fail together.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var created = await userManager.CreateAsync(identity, request.Password ?? string.Empty);
        if (!created.Succeeded)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["password"] = created.Errors.Select(e => e.Description).ToArray(),
            });
        }

        var portalUser = new PortalUser
        {
            Id = id,
            UserName = userName,
            Email = email,
            DisplayName = displayName,
            RoleId = role.Id,
            IsActive = true,
            Role = role,
        };
        db.PortalUsers.Add(portalUser);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var counts = await RoleCountsAsync(db, ct);
        return Results.Created($"/api/mc/v1/users/{id}", ToDto(portalUser, counts));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, UpdateUserRequest request, UserManager<ApplicationUser> userManager, AppDbContext db, CancellationToken ct)
    {
        var email = request.Email?.Trim() ?? string.Empty;
        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        var errors = Validate(email, displayName);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var user = await LoadAsync(db, id, ct);
        if (user is null)
        {
            return UserNotFound();
        }

        if (await EmailTakenAsync(db, email, exceptId: id, ct))
        {
            return Conflict("Email already in use");
        }

        user.Email = email;
        user.DisplayName = displayName;

        var identity = await userManager.FindByIdAsync(id.ToString());
        if (identity is not null)
        {
            identity.Email = email;
            identity.DisplayName = displayName;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(user, await RoleCountsAsync(db, ct)));
    }

    private static async Task<IResult> SetRoleAsync(
        Guid id, SetUserRoleRequest request, ClaimsPrincipal caller, AppDbContext db, CancellationToken ct)
    {
        var user = await LoadAsync(db, id, ct);
        if (user is null)
        {
            return UserNotFound();
        }

        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == request.RoleId, ct);
        if (role is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["roleId"] = ["Role does not exist."] });
        }

        if (role.Id != user.RoleId)
        {
            if (user.IsActive && IsAdministrator(user.Role) && await ActiveAdministratorsAsync(db, ct) <= 1)
            {
                return Conflict("The last active Administrator cannot be moved to another role");
            }

            // Same guard rail as for roles: do not lock yourself out of user and role management.
            if (CallerId(caller) == id && !SelfProtected.All(p => role.Permissions.Any(rp => rp.Permission == p)))
            {
                return Conflict("You cannot move yourself to a role without users.manage and roles.manage");
            }

            user.RoleId = role.Id;
            user.Role = role;
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(ToDto(user, await RoleCountsAsync(db, ct)));
    }

    private static async Task<IResult> DisableAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var user = await LoadAsync(db, id, ct);
        if (user is null)
        {
            return UserNotFound();
        }

        if (user.IsActive)
        {
            if (IsAdministrator(user.Role) && await ActiveAdministratorsAsync(db, ct) <= 1)
            {
                return Conflict("The last active Administrator cannot be disabled");
            }

            user.IsActive = false; // login and refresh both reject inactive users
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> EnableAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var user = await db.PortalUsers.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return UserNotFound();
        }

        if (!user.IsActive)
        {
            user.IsActive = true;
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    private static Task<PortalUser?> LoadAsync(AppDbContext db, Guid id, CancellationToken ct)
        => db.PortalUsers.Include(u => u.Role).ThenInclude(r => r.Permissions).FirstOrDefaultAsync(u => u.Id == id, ct);

    private static bool IsAdministrator(Role role) => role.IsSystem && role.Name == RoleNames.Administrator;

    private static Task<int> ActiveAdministratorsAsync(AppDbContext db, CancellationToken ct)
        => db.PortalUsers.CountAsync(u => u.IsActive && u.Role.IsSystem && u.Role.Name == RoleNames.Administrator, ct);

    private static async Task<bool> EmailTakenAsync(AppDbContext db, string email, Guid? exceptId, CancellationToken ct)
    {
        var lower = email.ToLowerInvariant();
        return await db.PortalUsers.AnyAsync(u => u.Email.ToLower() == lower && u.Id != exceptId, ct);
    }

    private static Task<Dictionary<Guid, int>> RoleCountsAsync(AppDbContext db, CancellationToken ct)
        => db.PortalUsers.AsNoTracking()
            .GroupBy(u => u.RoleId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    private static Guid? CallerId(ClaimsPrincipal caller)
        => Guid.TryParse(caller.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var id) ? id : null;

    private static Dictionary<string, string[]> Validate(string email, string displayName)
    {
        var errors = new Dictionary<string, string[]>();
        if (displayName.Length is 0 or > 100)
        {
            errors["displayName"] = ["DisplayName is required and must be at most 100 characters."];
        }

        if (!MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
        {
            errors["email"] = ["A valid email address is required."];
        }

        return errors;
    }

    private static PortalUserDto ToDto(PortalUser user, IReadOnlyDictionary<Guid, int> counts)
    {
        var permissions = user.Role.Permissions
            .Select(p => p.Permission)
            .Where(Permissions.All.Contains)
            .Distinct()
            .Order()
            .ToList();

        return new PortalUserDto(
            user.Id,
            user.UserName,
            user.Email,
            user.DisplayName,
            new RoleDto(user.Role.Id, user.Role.Name, user.Role.IsSystem, counts.GetValueOrDefault(user.RoleId), permissions),
            user.IsActive,
            permissions);
    }

    private static IResult Conflict(string title) => Results.Problem(title: title, statusCode: StatusCodes.Status409Conflict);

    private static IResult UserNotFound() => Results.Problem(title: "User not found", statusCode: StatusCodes.Status404NotFound);
}
