using System.Security.Claims;
using Heartbeat.Api.Auth;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Tests;

public class PermissionAuthorizationTests
{
    private static IAuthorizationService CreateService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddPermissionPolicies());
        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal User(string audience, params string[] permissions)
    {
        var claims = new List<Claim> { new("aud", audience), new("sub", Guid.NewGuid().ToString()) };
        claims.AddRange(permissions.Select(p => new Claim("perm", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task User_without_permission_is_forbidden()
    {
        var result = await CreateService().AuthorizeAsync(
            User(AuthConstants.McAudience, Permissions.CrewView),
            AuthConstants.PolicyFor(Permissions.VitalsView));

        Assert.False(result.Succeeded); // authenticated but not allowed → 403
    }

    [Fact]
    public async Task User_with_permission_is_allowed()
    {
        var result = await CreateService().AuthorizeAsync(
            User(AuthConstants.McAudience, Permissions.VitalsView),
            AuthConstants.PolicyFor(Permissions.VitalsView));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Crew_token_never_satisfies_a_permission_policy()
    {
        var result = await CreateService().AuthorizeAsync(
            User(AuthConstants.CrewAudience, Permissions.VitalsView),
            AuthConstants.PolicyFor(Permissions.VitalsView));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Permission_does_not_grant_a_different_permission()
    {
        var service = CreateService();
        var user = User(AuthConstants.McAudience, Permissions.AlertsView);

        Assert.True((await service.AuthorizeAsync(user, AuthConstants.PolicyFor(Permissions.AlertsView))).Succeeded);
        Assert.False((await service.AuthorizeAsync(user, AuthConstants.PolicyFor(Permissions.AlertsAcknowledge))).Succeeded);
    }

    [Fact]
    public void Every_permission_constant_has_its_own_policy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddPermissionPolicies());
        var options = services.BuildServiceProvider().GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthorizationOptions>>().Value;

        foreach (var permission in Permissions.All)
        {
            Assert.NotNull(options.GetPolicy(AuthConstants.PolicyFor(permission)));
        }
    }

    [Fact]
    public void RequirePermission_rejects_unknown_permissions()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        Assert.Throws<ArgumentException>(() => app.MapGet("/x", () => "x").RequirePermission("not.a.permission"));
    }
}
