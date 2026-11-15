using Heartbeat.Shared;
using Microsoft.AspNetCore.Authorization;

namespace Heartbeat.Api.Auth;

public static class AuthConstants
{
    public const string McAudience = "heartbeat-mc";
    public const string CrewAudience = "heartbeat-crew";
    public const string PermissionClaim = "perm";
    public const string McPolicy = "Mc";
    public const string CrewPolicy = "Crew";

    /// <summary>Datos de un astronauta (FC en vivo, Samsung Health): un tripulante, o MC con <c>vitals.view</c>.</summary>
    public const string AstronautDataPolicy = "AstronautData";

    public static string PolicyFor(string permission) => $"perm:{permission}";
}

/// <summary>The caller must hold the given permission (a <c>perm</c> claim) in an MC token.</summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var isMc = context.User.HasClaim("aud", AuthConstants.McAudience);
        if (isMc && context.User.HasClaim(AuthConstants.PermissionClaim, requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class PermissionAuthorizationExtensions
{
    /// <summary>Registers one policy per constant in <see cref="Permissions.All"/>.</summary>
    public static AuthorizationOptions AddPermissionPolicies(this AuthorizationOptions options)
    {
        foreach (var permission in Permissions.All)
        {
            options.AddPolicy(
                AuthConstants.PolicyFor(permission),
                policy => policy.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(permission)));
        }

        return options;
    }

    /// <summary>Registers <see cref="AuthConstants.AstronautDataPolicy"/>.</summary>
    public static AuthorizationOptions AddAstronautDataPolicy(this AuthorizationOptions options)
    {
        options.AddPolicy(
            AuthConstants.AstronautDataPolicy,
            policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                context.User.HasClaim("aud", AuthConstants.CrewAudience)
                || (context.User.HasClaim("aud", AuthConstants.McAudience)
                    && context.User.HasClaim(AuthConstants.PermissionClaim, Permissions.VitalsView))));
        return options;
    }

    /// <summary>Requires exactly this permission: unauthenticated → 401, missing permission → 403.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!Permissions.All.Contains(permission))
        {
            throw new ArgumentException($"Unknown permission '{permission}'. Use a constant from Heartbeat.Shared.Permissions.", nameof(permission));
        }

        return builder.RequireAuthorization(AuthConstants.PolicyFor(permission));
    }
}
