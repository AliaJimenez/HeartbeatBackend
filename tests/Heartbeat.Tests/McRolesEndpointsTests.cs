using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Tests;

public class McRolesEndpointsTests : IClassFixture<McRolesEndpointsTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
            => builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_dbPath}");

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(_dbPath); } catch (IOException) { }
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Factory _factory;
    private readonly Guid _adminRole = Guid.NewGuid();
    private readonly Guid _managerRole = Guid.NewGuid(); // custom role whose user is the caller
    private readonly Guid _emptyRole = Guid.NewGuid();
    private readonly Guid _usedRole = Guid.NewGuid();
    private readonly Guid _callerId = Guid.NewGuid();

    private static string Tag(Guid id) => id.ToString("N")[..8];
    private string ManagerName => "Manager-" + Tag(_managerRole);

    public McRolesEndpointsTests(Factory factory)
    {
        _factory = factory;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Role R(Guid id, string name, bool system, params string[] perms)
        {
            var role = new Role { Id = id, Name = name, IsSystem = system };
            foreach (var p in perms) role.Permissions.Add(new RolePermission { RoleId = id, Permission = p });
            return role;
        }

        db.Roles.AddRange(
            R(_adminRole, "Sys-" + Tag(_adminRole), true, Permissions.All.ToArray()),
            R(_managerRole, ManagerName, false, Permissions.UsersManage, Permissions.RolesManage, Permissions.CrewView),
            R(_emptyRole, "Empty-" + Tag(_emptyRole), false, Permissions.CrewView),
            R(_usedRole, "Used-" + Tag(_usedRole), false, Permissions.CrewView));
        db.SaveChanges();

        db.PortalUsers.AddRange(
            new PortalUser { Id = _callerId, UserName = "caller" + _callerId.ToString("N")[..6], Email = "c@x.io", DisplayName = "Caller", RoleId = _managerRole },
            new PortalUser { Id = Guid.NewGuid(), UserName = "u" + _usedRole.ToString("N")[..6], Email = "u@x.io", DisplayName = "U", RoleId = _usedRole });
        db.SaveChanges();
    }

    private HttpClient Client(Guid? userId = null, params string[] permissions)
    {
        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(userId ?? Guid.NewGuid(), "Dr", AuthConstants.McAudience, permissions: permissions));
        return client;
    }

    private static SaveRoleRequest Body(string name, params string[] perms) => new(name, perms);

    [Fact]
    public async Task Permissions_catalog_lists_every_permission_and_needs_roles_manage()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(null, Permissions.UsersView).GetAsync("/api/mc/v1/permissions")).StatusCode);

        var list = (await Client(null, Permissions.RolesManage).GetFromJsonAsync<List<PermissionDto>>("/api/mc/v1/permissions", Json))!;
        Assert.Equal(Permissions.All.Order(), list.Select(p => p.Key).Order());
        Assert.All(list, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public async Task List_roles_needs_users_view_or_roles_manage()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/mc/v1/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(null, Permissions.CrewView).GetAsync("/api/mc/v1/roles")).StatusCode);

        foreach (var perm in new[] { Permissions.UsersView, Permissions.RolesManage })
        {
            var roles = (await Client(null, perm).GetFromJsonAsync<List<RoleDto>>("/api/mc/v1/roles", Json))!;
            Assert.Equal(1, roles.Single(r => r.Id == _usedRole).UserCount);
            Assert.Equal(0, roles.Single(r => r.Id == _emptyRole).UserCount);
        }
    }

    [Fact]
    public async Task Create_role_returns_201_and_rejects_duplicates_and_unknown_permissions()
    {
        var http = Client(null, Permissions.RolesManage);
        var name = "Nurse " + Guid.NewGuid().ToString("N")[..6];

        var created = await http.PostAsJsonAsync("/api/mc/v1/roles", Body(name, Permissions.CrewView, Permissions.CrewView, Permissions.AlertsView));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = (await created.Content.ReadFromJsonAsync<RoleDto>(Json))!;
        Assert.False(dto.IsSystem);
        Assert.Equal([Permissions.AlertsView, Permissions.CrewView], dto.Permissions);

        var dup = await http.PostAsJsonAsync("/api/mc/v1/roles", Body(name.ToUpperInvariant(), Permissions.CrewView));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var bad = await http.PostAsJsonAsync("/api/mc/v1/roles", Body("Other " + Guid.NewGuid().ToString("N")[..6], "nope.view"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var blank = await http.PostAsJsonAsync("/api/mc/v1/roles", Body("  "));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    [Fact]
    public async Task Create_role_requires_roles_manage()
    {
        var res = await Client(null, Permissions.UsersManage).PostAsJsonAsync("/api/mc/v1/roles", Body("X", Permissions.CrewView));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Update_replaces_name_and_permissions()
    {
        var http = Client(null, Permissions.RolesManage);
        var res = await http.PutAsJsonAsync($"/api/mc/v1/roles/{_emptyRole}", Body("Renamed " + _emptyRole.ToString("N")[..6], Permissions.VitalsView));
        res.EnsureSuccessStatusCode();
        var dto = (await res.Content.ReadFromJsonAsync<RoleDto>(Json))!;
        Assert.Equal([Permissions.VitalsView], dto.Permissions);
        Assert.StartsWith("Renamed", dto.Name);

        var again = (await http.GetFromJsonAsync<List<RoleDto>>("/api/mc/v1/roles", Json))!.Single(r => r.Id == _emptyRole);
        Assert.Equal([Permissions.VitalsView], again.Permissions);
    }

    [Fact]
    public async Task Update_unknown_role_is_404()
    {
        var res = await Client(null, Permissions.RolesManage).PutAsJsonAsync($"/api/mc/v1/roles/{Guid.NewGuid()}", Body("X", Permissions.CrewView));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Caller_cannot_remove_users_manage_or_roles_manage_from_own_role()
    {
        var http = Client(_callerId, Permissions.RolesManage);

        var noUsers = await http.PutAsJsonAsync($"/api/mc/v1/roles/{_managerRole}", Body(ManagerName, Permissions.RolesManage));
        Assert.Equal(HttpStatusCode.Conflict, noUsers.StatusCode);

        var noRoles = await http.PutAsJsonAsync($"/api/mc/v1/roles/{_managerRole}", Body(ManagerName, Permissions.UsersManage));
        Assert.Equal(HttpStatusCode.Conflict, noRoles.StatusCode);

        var ok = await http.PutAsJsonAsync($"/api/mc/v1/roles/{_managerRole}", Body(ManagerName, Permissions.UsersManage, Permissions.RolesManage, Permissions.AlertsView));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Another_admin_may_edit_a_role_that_loses_those_permissions()
    {
        var res = await Client(null, Permissions.RolesManage).PutAsJsonAsync($"/api/mc/v1/roles/{_emptyRole}", Body("Plain " + _emptyRole.ToString("N")[..6]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Administrator_system_role_cannot_be_edited_or_deleted()
    {
        Guid adminId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existing = db.Roles.FirstOrDefault(r => r.Name == RoleNames.Administrator);
            if (existing is null)
            {
                existing = new Role { Id = Guid.NewGuid(), Name = RoleNames.Administrator, IsSystem = true };
                db.Roles.Add(existing);
                db.SaveChanges();
            }

            adminId = existing.Id;
        }

        var http = Client(null, Permissions.RolesManage);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PutAsJsonAsync($"/api/mc/v1/roles/{adminId}", Body("Administrator", Permissions.CrewView))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.DeleteAsync($"/api/mc/v1/roles/{adminId}")).StatusCode);
    }

    [Fact]
    public async Task System_roles_cannot_be_deleted()
    {
        var res = await Client(null, Permissions.RolesManage).DeleteAsync($"/api/mc/v1/roles/{_adminRole}");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_role_with_users_is_409()
    {
        var res = await Client(null, Permissions.RolesManage).DeleteAsync($"/api/mc/v1/roles/{_usedRole}");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Deleting_an_unused_custom_role_returns_204_then_404()
    {
        var http = Client(null, Permissions.RolesManage);
        var created = await http.PostAsJsonAsync("/api/mc/v1/roles", Body("Temp " + Guid.NewGuid().ToString("N")[..6], Permissions.CrewView));
        var id = (await created.Content.ReadFromJsonAsync<RoleDto>(Json))!.Id;

        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/mc/v1/roles/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync($"/api/mc/v1/roles/{id}")).StatusCode);
    }
}
