using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Tests;

public class McUsersEndpointsTests : IClassFixture<McUsersEndpointsTests.Factory>
{
    private const string Password = "secret1";

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-{Guid.NewGuid():N}.db");
        private readonly object _lock = new();
        private bool _seeded;

        public Guid AdminRoleId { get; } = Guid.NewGuid();
        public Guid ViewerRoleId { get; } = Guid.NewGuid();
        public Guid AdminUserId { get; } = Guid.NewGuid();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
            => builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_dbPath}");

        public void EnsureSeeded()
        {
            lock (_lock)
            {
                if (_seeded) return;
                using var scope = Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var admin = new Role { Id = AdminRoleId, Name = RoleNames.Administrator, IsSystem = true };
                foreach (var p in Permissions.All) admin.Permissions.Add(new RolePermission { RoleId = AdminRoleId, Permission = p });
                var viewer = new Role { Id = ViewerRoleId, Name = "Viewer", IsSystem = false };
                viewer.Permissions.Add(new RolePermission { RoleId = ViewerRoleId, Permission = Permissions.CrewView });
                db.Roles.AddRange(admin, viewer);
                db.SaveChanges();

                AddUser(scope, db, AdminUserId, "admin", admin.Id).GetAwaiter().GetResult();
                _seeded = true;
            }
        }

        public static async Task AddUser(IServiceScope scope, AppDbContext db, Guid id, string userName, Guid roleId)
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var identity = new ApplicationUser { Id = id, UserName = userName, Email = $"{userName}@x.io", DisplayName = userName, AccountType = AccountType.Portal };
            Assert.True((await um.CreateAsync(identity, Password)).Succeeded);
            db.PortalUsers.Add(new PortalUser { Id = id, UserName = userName, Email = $"{userName}@x.io", DisplayName = userName, RoleId = roleId, IsActive = true });
            await db.SaveChangesAsync();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(_dbPath); } catch (IOException) { }
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Factory _factory;

    public McUsersEndpointsTests(Factory factory)
    {
        _factory = factory;
        factory.EnsureSeeded();
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private HttpClient Client(Guid? userId = null, params string[] permissions)
    {
        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(userId ?? Guid.NewGuid(), "Dr", AuthConstants.McAudience, permissions: permissions));
        return client;
    }

    private HttpClient Manager(Guid? userId = null) => Client(userId, Permissions.UsersManage, Permissions.UsersView);

    private async Task<PortalUserDto> CreateUser(Guid roleId, string? userName = null)
    {
        userName ??= Unique("u");
        var res = await Manager().PostAsJsonAsync("/api/mc/v1/users",
            new CreateUserRequest(userName, $"{userName}@heartbeat.demo", "User " + userName, Password, roleId));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<PortalUserDto>(Json))!;
    }

    private async Task<McLoginResponse> Login(string userName)
    {
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/mc/v1/auth/login", new LoginRequest(userName, Password, "dev"));
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<McLoginResponse>(Json))!;
    }

    private static string[] PermsIn(string accessToken)
        => new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.Where(c => c.Type == "perm").Select(c => c.Value).Order().ToArray();

    [Fact]
    public async Task Endpoints_require_their_permissions()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/mc/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(null, Permissions.CrewView).GetAsync("/api/mc/v1/users")).StatusCode);

        var body = new CreateUserRequest("x", "x@x.io", "X", Password, _factory.ViewerRoleId);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(null, Permissions.UsersView).PostAsJsonAsync("/api/mc/v1/users", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(null, Permissions.UsersView).PostAsync($"/api/mc/v1/users/{Guid.NewGuid()}/disable", null)).StatusCode);
    }

    [Fact]
    public async Task Create_user_can_log_in_with_the_role_permissions()
    {
        var created = await CreateUser(_factory.ViewerRoleId);
        Assert.True(created.IsActive);
        Assert.Equal("Viewer", created.Role.Name);

        var login = await Login(created.UserName);
        Assert.Equal([Permissions.CrewView], PermsIn(login.AccessToken));
    }

    [Fact]
    public async Task Create_user_rejects_duplicates_bad_input_and_unknown_role()
    {
        var existing = await CreateUser(_factory.ViewerRoleId);
        var http = Manager();

        var dupName = await http.PostAsJsonAsync("/api/mc/v1/users", new CreateUserRequest(existing.UserName, Unique("a") + "@x.io", "A", Password, _factory.ViewerRoleId));
        Assert.Equal(HttpStatusCode.Conflict, dupName.StatusCode);

        var dupEmail = await http.PostAsJsonAsync("/api/mc/v1/users", new CreateUserRequest(Unique("b"), existing.Email.ToUpperInvariant(), "B", Password, _factory.ViewerRoleId));
        Assert.Equal(HttpStatusCode.Conflict, dupEmail.StatusCode);

        var badEmail = await http.PostAsJsonAsync("/api/mc/v1/users", new CreateUserRequest(Unique("c"), "not-an-email", "C", Password, _factory.ViewerRoleId));
        Assert.Equal(HttpStatusCode.BadRequest, badEmail.StatusCode);

        var shortPassword = await http.PostAsJsonAsync("/api/mc/v1/users", new CreateUserRequest(Unique("d"), Unique("d") + "@x.io", "D", "123", _factory.ViewerRoleId));
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);

        var noRole = await http.PostAsJsonAsync("/api/mc/v1/users", new CreateUserRequest(Unique("e"), Unique("e") + "@x.io", "E", Password, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, noRole.StatusCode);
    }

    [Fact]
    public async Task List_is_paged_and_includes_role_and_permissions()
    {
        await CreateUser(_factory.ViewerRoleId);
        await CreateUser(_factory.ViewerRoleId);
        var http = Manager();

        var page = (await http.GetFromJsonAsync<Paged<PortalUserDto>>("/api/mc/v1/users?page=1&pageSize=2", Json))!;
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.Total >= 3);
        Assert.All(page.Items, u => Assert.NotEmpty(u.Permissions));

        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/mc/v1/users?pageSize=0")).StatusCode);
    }

    [Fact]
    public async Task Update_changes_email_and_display_name()
    {
        var user = await CreateUser(_factory.ViewerRoleId);
        var email = Unique("new") + "@heartbeat.demo";

        var res = await Manager().PutAsJsonAsync($"/api/mc/v1/users/{user.Id}", new UpdateUserRequest(email, "Renamed"));
        res.EnsureSuccessStatusCode();
        var dto = (await res.Content.ReadFromJsonAsync<PortalUserDto>(Json))!;
        Assert.Equal((email, "Renamed"), (dto.Email, dto.DisplayName));

        var taken = await Manager().PutAsJsonAsync($"/api/mc/v1/users/{user.Id}", new UpdateUserRequest("admin@x.io", "Renamed"));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await Manager().PutAsJsonAsync($"/api/mc/v1/users/{Guid.NewGuid()}", new UpdateUserRequest(email, "X"))).StatusCode);
    }

    [Fact]
    public async Task Role_change_applies_on_next_refresh()
    {
        var user = await CreateUser(_factory.ViewerRoleId);
        var login = await Login(user.UserName);
        Assert.Equal([Permissions.CrewView], PermsIn(login.AccessToken));

        var res = await Manager().PutAsJsonAsync($"/api/mc/v1/users/{user.Id}/role", new SetUserRoleRequest(_factory.AdminRoleId));
        res.EnsureSuccessStatusCode();
        Assert.Equal("Administrator", (await res.Content.ReadFromJsonAsync<PortalUserDto>(Json))!.Role.Name);

        // The token already issued is unchanged...
        Assert.Equal([Permissions.CrewView], PermsIn(login.AccessToken));

        // ...and the next refresh carries the new role.
        var refresh = await _factory.CreateClient().PostAsJsonAsync("/api/mc/v1/auth/refresh", new RefreshRequest(login.RefreshToken));
        refresh.EnsureSuccessStatusCode();
        var tokens = (await refresh.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        Assert.Equal(Permissions.All.Order(), PermsIn(tokens.AccessToken));
    }

    [Fact]
    public async Task Set_role_validates_user_and_role()
    {
        var user = await CreateUser(_factory.ViewerRoleId);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Manager().PutAsJsonAsync($"/api/mc/v1/users/{Guid.NewGuid()}/role", new SetUserRoleRequest(_factory.ViewerRoleId))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Manager().PutAsJsonAsync($"/api/mc/v1/users/{user.Id}/role", new SetUserRoleRequest(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task Disabled_user_cannot_log_in_or_refresh_until_enabled()
    {
        var user = await CreateUser(_factory.ViewerRoleId);
        var login = await Login(user.UserName);
        var http = Manager();

        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync($"/api/mc/v1/users/{user.Id}/disable", null)).StatusCode);

        var relogin = await _factory.CreateClient().PostAsJsonAsync("/api/mc/v1/auth/login", new LoginRequest(user.UserName, Password, "dev"));
        Assert.Equal(HttpStatusCode.Forbidden, relogin.StatusCode);
        var refresh = await _factory.CreateClient().PostAsJsonAsync("/api/mc/v1/auth/refresh", new RefreshRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync($"/api/mc/v1/users/{user.Id}/enable", null)).StatusCode);
        await Login(user.UserName); // works again

        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"/api/mc/v1/users/{Guid.NewGuid()}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"/api/mc/v1/users/{Guid.NewGuid()}/enable", null)).StatusCode);
    }

    [Fact]
    public async Task Last_active_administrator_cannot_be_disabled_or_demoted()
    {
        // Leave the seeded admin as the only active Administrator.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PortalUsers.Where(u => u.RoleId == _factory.AdminRoleId && u.Id != _factory.AdminUserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        var http = Manager();
        var admin = _factory.AdminUserId;
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/api/mc/v1/users/{admin}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await http.PutAsJsonAsync($"/api/mc/v1/users/{admin}/role", new SetUserRoleRequest(_factory.ViewerRoleId))).StatusCode);

        // With a second active Administrator, the first can be disabled.
        var second = await CreateUser(_factory.AdminRoleId);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync($"/api/mc/v1/users/{admin}/disable", null)).StatusCode);

        // And now the second one is the last.
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/api/mc/v1/users/{second.Id}/disable", null)).StatusCode);

        await http.PostAsync($"/api/mc/v1/users/{admin}/enable", null); // restore for other tests
    }

    [Fact]
    public async Task Caller_cannot_move_themselves_to_a_role_without_user_and_role_management()
    {
        var other = await CreateUser(_factory.AdminRoleId); // so this admin is not the last one
        var res = await Manager(other.Id).PutAsJsonAsync($"/api/mc/v1/users/{other.Id}/role", new SetUserRoleRequest(_factory.ViewerRoleId));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);

        // Someone else may make that change.
        var ok = await Manager().PutAsJsonAsync($"/api/mc/v1/users/{other.Id}/role", new SetUserRoleRequest(_factory.ViewerRoleId));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}
