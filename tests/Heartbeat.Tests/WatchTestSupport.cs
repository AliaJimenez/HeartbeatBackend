using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Api.Live;
using Heartbeat.Api.Salud;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Heartbeat.Tests;

/// <summary>
/// WatchSync (el backend Python) simulado como <see cref="HttpMessageHandler"/>, con sesión: las rutas de datos responden
/// 401 hasta que alguien hace <c>POST auth/login</c> con la cuenta correcta, como el servidor real.
/// </summary>
public sealed class FakeWatchSyncServer : HttpMessageHandler
{
    public const string Email = "fake@example.com";
    public const string Password = "pw";
    public static readonly Uri BaseAddress = new("http://watchsync.test/api/v1/");

    public sealed record Call(HttpMethod Method, string PathAndQuery, string? Body);

    private readonly object _gate = new();
    private readonly List<Call> _calls = [];
    private bool _loggedIn;

    /// <summary>Respuesta de las rutas de datos (ya con sesión). Por defecto, un JSON de ejemplo según la ruta.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> RespondAsync { get; set; }

    /// <summary>Si se define, sustituye al login (para simular que rechaza o no responde).</summary>
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? LoginAsync { get; set; }

    public FakeWatchSyncServer() => RespondAsync = (request, _) => Task.FromResult(Json(Canned(request.RequestUri!.AbsolutePath)));

    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToList();
            }
        }
    }

    public int LoginCount => Calls.Count(c => c.PathAndQuery.EndsWith("/auth/login", StringComparison.Ordinal));

    public IReadOnlyList<Call> DataCalls => Calls.Where(c => !c.PathAndQuery.EndsWith("/auth/login", StringComparison.Ordinal)).ToList();

    public void ExpireSession()
    {
        lock (_gate)
        {
            _loggedIn = false;
        }
    }

    public void Respond(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => RespondAsync = (request, _) => Task.FromResult(respond(request));

    public WatchSyncClient CreateClient(TimeSpan? timeout = null)
        => new(new HttpClient(this) { BaseAddress = BaseAddress, Timeout = timeout ?? TimeSpan.FromSeconds(10) }, Email, Password);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_gate)
        {
            _calls.Add(new Call(request.Method, request.RequestUri!.PathAndQuery, body));
        }

        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/auth/login", StringComparison.Ordinal))
        {
            if (LoginAsync is not null)
            {
                return await LoginAsync(request);
            }

            using var doc = JsonDocument.Parse(body ?? "{}");
            var ok = doc.RootElement.TryGetProperty("email", out var email) && email.GetString() == Email
                     && doc.RootElement.TryGetProperty("password", out var password) && password.GetString() == Password;
            if (!ok)
            {
                return Error(HttpStatusCode.Unauthorized, "invalid_credentials", "Credenciales inválidas");
            }

            lock (_gate)
            {
                _loggedIn = true;
            }

            return Json("{}");
        }

        bool loggedIn;
        lock (_gate)
        {
            loggedIn = _loggedIn;
        }

        return loggedIn
            ? await RespondAsync(request, cancellationToken)
            : Error(HttpStatusCode.Unauthorized, "unauthorized", "Sesión requerida");
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(HttpStatusCode status, string? code, string? message)
    {
        var fields = new List<string>();
        if (code is not null)
        {
            fields.Add($"\"code\":\"{code}\"");
        }

        if (message is not null)
        {
            fields.Add($"\"message\":\"{message}\"");
        }

        return Json("{" + string.Join(",", fields) + "}", status);
    }

    private static string Canned(string path) => path switch
    {
        _ when path.EndsWith("/summary", StringComparison.Ordinal) =>
            """{"date":"2026-11-15","latest":{"heart_rate":{"value":72,"unit":"lpm","measured_at":"2026-11-15T08:00:00+00:00"}},"unacknowledged_alerts":2}""",
        _ when path.EndsWith("/metrics/heart_rate", StringComparison.Ordinal) =>
            """{"metric":"heart_rate","unit":"lpm","resolution":"1h","points":[{"t":"2026-11-15T08:00:00+00:00","avg":70,"min":60,"max":80,"count":3600}]}""",
        _ when path.EndsWith("/sync/status", StringComparison.Ordinal) =>
            """[{"data_type":"heart_rate","last_synced_at":"2026-11-15T08:05:00+00:00","last_measured_at":"2026-11-15T08:00:00+00:00"}]""",
        _ => "[]",
    };
}

/// <summary>Entrega siempre el mismo cliente de WatchSync (como el real, uno por cuenta) y anota para quién se pidió.</summary>
public sealed class ScriptedWatchSyncClients(FakeWatchSyncServer server) : IWatchSyncClients
{
    private readonly object _gate = new();
    private WatchSyncClient? _client;

    /// <summary>Tiempo máximo de cada llamada; se fija antes de la primera petición.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public List<string?> Requested { get; } = [];

    public WatchSyncClient For(string? astronautUserName)
    {
        lock (_gate)
        {
            Requested.Add(astronautUserName);
            return _client ??= server.CreateClient(Timeout);
        }
    }
}

/// <summary>
/// Base de las pruebas de la API del reloj: una API con su propia base SQLite, dos astronautas (Ana y Ben), un WatchSync
/// falso opcional y ayudas para tokens, relojes registrados y lecturas.
/// </summary>
public abstract class WatchApiTestBase : IDisposable
{
    protected sealed class Factory(IWatchSyncClients? clients) : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_dbPath}");
            if (clients is not null)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IWatchSyncClients>();
                    services.AddSingleton(clients);
                });
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(_dbPath); } catch (IOException) { }
        }
    }

    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    protected readonly Factory Api;
    protected readonly Guid AnaId = Guid.NewGuid();
    protected readonly Guid BenId = Guid.NewGuid();

    protected WatchApiTestBase(IWatchSyncClients? clients = null)
    {
        Api = new Factory(clients);
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mission = new Mission { Id = Guid.NewGuid(), Name = "Demo", StartsAt = DateTimeOffset.UtcNow.AddDays(-5) };
        db.Missions.Add(mission);
        db.Astronauts.AddRange(
            new Astronaut { Id = AnaId, UserName = "ana", DisplayName = "Ana", MissionId = mission.Id, UpdatedAt = DateTimeOffset.UtcNow },
            new Astronaut { Id = BenId, UserName = "ben", DisplayName = "Ben", MissionId = mission.Id, UpdatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
    }

    public void Dispose() => Api.Dispose();

    protected string Token(string audience, Guid userId, params string[] permissions)
        => Api.Services.GetRequiredService<JwtTokenService>().CreateAccessToken(userId, "Someone", audience, permissions: permissions);

    protected HttpClient Authorized(string audience, Guid userId, params string[] permissions)
    {
        var client = Api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(audience, userId, permissions));
        return client;
    }

    protected HttpClient Crew(Guid astronautId) => Authorized(AuthConstants.CrewAudience, astronautId);

    protected HttpClient Mc(params string[] permissions) => Authorized(AuthConstants.McAudience, Guid.NewGuid(), permissions);

    /// <summary>Registra un reloj del astronauta directamente en la base y devuelve su clave.</summary>
    protected async Task<string> SeedWatchAsync(Guid astronautId, string? deviceId = null)
    {
        var key = WatchKeys.NewKey();
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.WatchDevices.Add(new WatchDevice
        {
            Id = Guid.CreateVersion7(),
            DeviceId = deviceId ?? $"w-{Guid.NewGuid():N}",
            KeyHash = WatchKeys.Hash(key),
            AstronautId = astronautId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return key;
    }

    /// <summary>POST /api/heartrate como lo hace el reloj: cabecera con la clave y el cuerpo fijo {bpm, measuredAt, source}.</summary>
    protected async Task<HttpResponseMessage> PostReadingAsync(
        string? key, int bpm = 72, DateTimeOffset? measuredAt = null, string? source = "test")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/heartrate")
        {
            Content = JsonContent.Create(new HeartRateReading(bpm, measuredAt ?? DateTimeOffset.UtcNow, source), options: Json),
        };
        if (key is not null)
        {
            request.Headers.Add(WatchKeys.HeaderName, key);
        }

        return await Api.CreateClient().SendAsync(request);
    }

    protected async Task<List<HeartRateReading>> RecentAsync(Guid astronautId, string query = "")
    {
        var res = await Crew(astronautId).GetAsync($"/api/heartrate/recent{query}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<List<HeartRateReading>>(Json))!;
    }
}
