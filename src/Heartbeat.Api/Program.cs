using Heartbeat.Api.Alerts;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Crew;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Api.Data.Seeding;
using Heartbeat.Api.Deployment;
using Heartbeat.Api.Live;
using Heartbeat.Api.Messages;
using Heartbeat.Api.Roles;
using Heartbeat.Api.Salud;
using Heartbeat.Api.Users;
using Heartbeat.Api.Vitals;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// CONTRACTS.md §1: enums travel as camelCase strings.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

builder.Services.AddSingleton<Heartbeat.Api.Vitals.RangesCatalog>();
builder.Services.AddScoped<IDataSeeder, DemoDataSeeder>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(DeploymentSettings.ResolveSqliteConnectionString(
        builder.Configuration.GetConnectionString("DefaultConnection"), builder.Environment.ContentRootPath)));

builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 6;
})
.AddEntityFrameworkStores<AppDbContext>();

var jwtKey = DeploymentSettings.ResolveJwtKey(builder.Configuration, builder.Environment);
// AddIdentity registers cookie auth as the default scheme; the APIs are bearer-only, so override all defaults
// (otherwise tokens are never read and anonymous calls get redirected to /Account/Login instead of a 401).
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultForbidScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "Heartbeat",
            ValidateAudience = true,
            ValidAudiences = new[] { "heartbeat-crew", "heartbeat-mc" },
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthConstants.CrewPolicy, policy => policy.RequireAuthenticatedUser().RequireClaim("aud", AuthConstants.CrewAudience));
    options.AddPolicy(AuthConstants.McPolicy, policy => policy.RequireAuthenticatedUser().RequireClaim("aud", AuthConstants.McAudience));
    options.AddPermissionPolicies();
    options.AddRolePolicies();
});

builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

builder.Services.AddSingleton<JwtTokenService>();

// FC en vivo del Galaxy Watch 5 (Live/): búfer en memoria y difusión por SignalR.
builder.Services.AddSignalR();
builder.Services.AddSingleton<HeartRateBuffer>();

// Datos de Samsung Health desde el backend Python WatchSync (Salud/). La contraseña va en user-secrets.
builder.Services.Configure<WatchSyncOptions>(builder.Configuration.GetSection("WatchSync"));
builder.Services.AddSingleton<System.Net.CookieContainer>();
builder.Services.AddHttpClient<WatchSyncClient>((sp, http) =>
{
    var o = sp.GetRequiredService<IOptions<WatchSyncOptions>>().Value;
    http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(10);
}).ConfigurePrimaryHttpMessageHandler(sp => new HttpClientHandler
{
    UseCookies = true,
    CookieContainer = sp.GetRequiredService<System.Net.CookieContainer>(),
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    // Demo data is opt-in per environment (Seed__Enabled=true) and lives in IDataSeeder implementations (#10).
    if (app.Configuration.GetValue<bool>("Seed:Enabled"))
    {
        foreach (var seeder in scope.ServiceProvider.GetServices<IDataSeeder>())
        {
            await seeder.SeedAsync(scope.ServiceProvider, CancellationToken.None);
        }
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// El reloj llama por HTTP sin TLS y no sigue redirecciones: las rutas en vivo quedan fuera de la redirección a HTTPS.
app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/api/heartrate")
                   && !ctx.Request.Path.StartsWithSegments("/hubs/heartrate"),
            branch => branch.UseHttpsRedirection());
app.UseAuthentication();
app.UseAuthorization();

// Anonymous smoke test for the deployment: confirms the app is up and the database is reachable.
app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ok", rangesVersion = Heartbeat.Shared.ReferenceRanges.Version })
        : Results.Problem(title: "Database unavailable", statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapAuthEndpoints();
app.MapMcCrewEndpoints();
app.MapMcMissionEndpoints();
app.MapMcPhantomsEndpoints();
app.MapMcProfilesEndpoints();
app.MapMcVitalsEndpoints();
app.MapMcRolesEndpoints();
app.MapMcUsersEndpoints();
app.MapMcAlertsEndpoints();
app.MapMcMessagesEndpoints();
app.MapCrewEndpoints();
app.MapHub<HeartRateHub>("/hubs/heartrate");
app.MapLiveHeartRate();
app.MapSalud();

app.Run();

public partial class Program;
