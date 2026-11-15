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
using Heartbeat.Shared;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Heartbeat.Tests;

/// <summary>WR-BIND (#152): each watch belongs to an astronaut, /api/heartrate needs its key, reading needs a token.</summary>
public class WatchBindingTests : IDisposable
{
    private sealed class FakeWatchSyncClients : IWatchSyncClients
    {
        public List<string?> Requested { get; } = [];

        public WatchSyncClient For(string? astronautUserName)
        {
            lock (Requested)
            {
                Requested.Add(astronautUserName);
            }

            var http = new HttpClient(new Backend()) { BaseAddress = new Uri("http://watchsync.test/api/v1/") };
            return new WatchSyncClient(http, "fake@example.com", "pw");
        }

        private sealed class Backend : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                });
        }
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-{Guid.NewGuid():N}.db");

        public FakeWatchSyncClients WatchSync { get; } = new();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_dbPath}");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IWatchSyncClients>();
                services.AddSingleton<IWatchSyncClients>(WatchSync);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(_dbPath); } catch (IOException) { }
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private const string Hub = "/hubs/heartrate";

    private readonly Factory _factory = new();
    private readonly Guid _anaId = Guid.NewGuid();
    private readonly Guid _benId = Guid.NewGuid();

    public void Dispose() => _factory.Dispose();

    public WatchBindingTests()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mission = new Mission { Id = Guid.NewGuid(), Name = "Demo", StartsAt = DateTimeOffset.UtcNow.AddDays(-5) };
        db.Missions.Add(mission);
        db.Astronauts.AddRange(
            new Astronaut { Id = _anaId, UserName = "ana", DisplayName = "Ana", MissionId = mission.Id, UpdatedAt = DateTimeOffset.UtcNow },
            new Astronaut { Id = _benId, UserName = "ben", DisplayName = "Ben", MissionId = mission.Id, UpdatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
    }

    private string Token(string audience, Guid userId, params string[] permissions)
        => _factory.Services.GetRequiredService<JwtTokenService>().CreateAccessToken(userId, "Someone", audience, permissions: permissions);

    private HttpClient Authorized(string audience, Guid userId, params string[] permissions)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(audience, userId, permissions));
        return client;
    }

    private HttpClient Crew(Guid astronautId) => Authorized(AuthConstants.CrewAudience, astronautId);

    private HttpClient Mc(params string[] permissions) => Authorized(AuthConstants.McAudience, Guid.NewGuid(), permissions);

    private async Task<string> RegisterAsync(Guid astronautId, string deviceId)
    {
        var res = await Mc(Permissions.CrewManage).PostAsJsonAsync("/api/mc/v1/watches", new RegisterWatchRequest(astronautId, deviceId), Json);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<WatchRegisteredDto>(Json))!.Key;
    }

    private async Task<HttpResponseMessage> PostReadingAsync(string? key, int bpm = 72)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/heartrate")
        {
            Content = JsonContent.Create(new HeartRateReading(bpm, DateTimeOffset.UtcNow, "test"), options: Json),
        };
        if (key is not null)
        {
            request.Headers.Add(WatchKeys.HeaderName, key);
        }

        return await _factory.CreateClient().SendAsync(request);
    }

    // ----- Watch registry (MC, crew.manage) -----

    [Fact]
    public async Task The_registry_requires_crew_manage()
    {
        var body = new RegisterWatchRequest(_anaId, "w-1");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/mc/v1/watches", body, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Mc(Permissions.VitalsView).PostAsJsonAsync("/api/mc/v1/watches", body, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Crew(_anaId).PostAsJsonAsync("/api/mc/v1/watches", body, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Mc(Permissions.VitalsView).GetAsync("/api/mc/v1/watches")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Mc(Permissions.VitalsView).DeleteAsync($"/api/mc/v1/watches/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Mc(Permissions.VitalsView).PostAsync($"/api/mc/v1/watches/{Guid.NewGuid()}/rotate-key", null)).StatusCode);
    }

    [Fact]
    public async Task Registering_returns_the_key_once_and_stores_only_its_hash()
    {
        var res = await Mc(Permissions.CrewManage).PostAsJsonAsync(
            "/api/mc/v1/watches", new RegisterWatchRequest(_anaId, "  galaxy-watch-5-ana ", "Ana's watch"), Json);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var registered = (await res.Content.ReadFromJsonAsync<WatchRegisteredDto>(Json))!;
        Assert.Equal("galaxy-watch-5-ana", registered.Device.DeviceId);
        Assert.Equal(_anaId, registered.Device.AstronautId);
        Assert.Equal("Ana's watch", registered.Device.Label);
        Assert.True(registered.Key.Length >= 40);

        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().WatchDevices.SingleAsync();
        Assert.NotEqual(registered.Key, stored.KeyHash);
        Assert.Equal(WatchKeys.Hash(registered.Key), stored.KeyHash);

        var list = await Mc(Permissions.CrewManage).GetStringAsync($"/api/mc/v1/watches?astronautId={_anaId}");
        Assert.Contains("galaxy-watch-5-ana", list);
        Assert.DoesNotContain(registered.Key, list);
        Assert.DoesNotContain("keyHash", list, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Registering_validates_the_body()
    {
        var mc = Mc(Permissions.CrewManage);
        await RegisterAsync(_anaId, "w-1");

        Assert.Equal(HttpStatusCode.Conflict, (await mc.PostAsJsonAsync("/api/mc/v1/watches", new RegisterWatchRequest(_benId, "w-1"), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mc.PostAsJsonAsync("/api/mc/v1/watches", new RegisterWatchRequest(Guid.NewGuid(), "w-2"), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mc.PostAsJsonAsync("/api/mc/v1/watches", new RegisterWatchRequest(_anaId, "   "), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await mc.PostAsJsonAsync("/api/mc/v1/watches", new RegisterWatchRequest(_anaId, new string('x', WatchDevice.DeviceIdMaxLength + 1)), Json)).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_watch_revokes_its_key()
    {
        var key = await RegisterAsync(_anaId, "w-1");
        Assert.Equal(HttpStatusCode.OK, (await PostReadingAsync(key)).StatusCode);

        var watch = (await Mc(Permissions.CrewManage).GetFromJsonAsync<List<WatchDeviceDto>>("/api/mc/v1/watches", Json))!.Single();
        Assert.Equal(HttpStatusCode.NoContent, (await Mc(Permissions.CrewManage).DeleteAsync($"/api/mc/v1/watches/{watch.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostReadingAsync(key)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Mc(Permissions.CrewManage).DeleteAsync($"/api/mc/v1/watches/{watch.Id}")).StatusCode);
    }

    [Fact]
    public async Task Rotating_the_key_replaces_it()
    {
        var oldKey = await RegisterAsync(_anaId, "w-1");
        var watch = (await Mc(Permissions.CrewManage).GetFromJsonAsync<List<WatchDeviceDto>>("/api/mc/v1/watches", Json))!.Single();

        var res = await Mc(Permissions.CrewManage).PostAsync($"/api/mc/v1/watches/{watch.Id}/rotate-key", null);
        var newKey = (await res.Content.ReadFromJsonAsync<WatchRegisteredDto>(Json))!.Key;

        Assert.NotEqual(oldKey, newKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostReadingAsync(oldKey)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostReadingAsync(newKey)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Mc(Permissions.CrewManage).PostAsync($"/api/mc/v1/watches/{Guid.NewGuid()}/rotate-key", null)).StatusCode);
    }

    // ----- POST /api/heartrate needs the watch key -----

    [Fact]
    public async Task Posting_without_or_with_an_unknown_key_is_401()
    {
        await RegisterAsync(_anaId, "w-1");

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostReadingAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostReadingAsync("")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostReadingAsync("not-a-registered-key")).StatusCode);
        Assert.Null(await LatestOf(_anaId));
    }

    [Fact]
    public async Task A_user_token_is_not_a_watch_key()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/heartrate")
        {
            Content = JsonContent.Create(new HeartRateReading(72, DateTimeOffset.UtcNow, "test"), options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(AuthConstants.CrewAudience, _anaId));

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_valid_key_stores_the_reading_only_for_its_astronaut()
    {
        var anaKey = await RegisterAsync(_anaId, "w-ana");
        await RegisterAsync(_benId, "w-ben");

        Assert.Equal(HttpStatusCode.OK, (await PostReadingAsync(anaKey, 80)).StatusCode);

        Assert.Equal(80, (await LatestOf(_anaId))!.Bpm);
        Assert.Null(await LatestOf(_benId));
        Assert.Equal(HttpStatusCode.NoContent, (await Crew(_benId).GetAsync("/api/heartrate/latest")).StatusCode);
    }

    [Fact]
    public async Task Out_of_range_bpm_is_400_with_a_valid_key()
    {
        var key = await RegisterAsync(_anaId, "w-ana");

        Assert.Equal(HttpStatusCode.BadRequest, (await PostReadingAsync(key, LiveEndpoints.MinBpm - 1)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostReadingAsync(key, LiveEndpoints.MaxBpm + 1)).StatusCode);
        Assert.Null(await LatestOf(_anaId));
    }

    private async Task<HeartRateReading?> LatestOf(Guid astronautId)
    {
        var res = await Crew(astronautId).GetAsync("/api/heartrate/latest");
        return res.StatusCode == HttpStatusCode.NoContent ? null : await res.Content.ReadFromJsonAsync<HeartRateReading>(Json);
    }

    // ----- GET /latest and /recent need a token: the crew sees their own, MC with vitals.view sees any -----

    [Theory]
    [InlineData("/api/heartrate/latest")]
    [InlineData("/api/heartrate/recent")]
    public async Task Reading_without_a_token_is_401(string path)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("/api/heartrate/latest")]
    [InlineData("/api/heartrate/recent")]
    public async Task A_crew_member_cannot_ask_for_another_astronaut(string path)
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Crew(_anaId).GetAsync($"{path}?astronautId={_benId}")).StatusCode);
        var own = (await Crew(_anaId).GetAsync($"{path}?astronautId={_anaId}")).StatusCode;
        Assert.True(own is HttpStatusCode.OK or HttpStatusCode.NoContent, $"own astronautId gave {own}");
    }

    [Fact]
    public async Task A_crew_member_sees_only_their_own_recent_readings()
    {
        var anaKey = await RegisterAsync(_anaId, "w-ana");
        var benKey = await RegisterAsync(_benId, "w-ben");
        await PostReadingAsync(anaKey, 70);
        await PostReadingAsync(anaKey, 71);
        await PostReadingAsync(benKey, 120);

        var ana = (await Crew(_anaId).GetFromJsonAsync<List<HeartRateReading>>("/api/heartrate/recent", Json))!;
        var ben = (await Crew(_benId).GetFromJsonAsync<List<HeartRateReading>>("/api/heartrate/recent?seconds=60", Json))!;

        Assert.Equal([70, 71], ana.Select(r => r.Bpm));
        Assert.Equal([120], ben.Select(r => r.Bpm));
    }

    [Fact]
    public async Task Mission_control_with_vitals_view_sees_any_astronaut_and_must_choose_one()
    {
        var anaKey = await RegisterAsync(_anaId, "w-ana");
        var benKey = await RegisterAsync(_benId, "w-ben");
        await PostReadingAsync(anaKey, 75);
        await PostReadingAsync(benKey, 95);
        var mc = Mc(Permissions.VitalsView);

        Assert.Equal(75, (await mc.GetFromJsonAsync<HeartRateReading>($"/api/heartrate/latest?astronautId={_anaId}", Json))!.Bpm);
        Assert.Equal(95, (await mc.GetFromJsonAsync<HeartRateReading>($"/api/heartrate/latest?astronautId={_benId}", Json))!.Bpm);
        Assert.Equal([95], (await mc.GetFromJsonAsync<List<HeartRateReading>>($"/api/heartrate/recent?astronautId={_benId}", Json))!.Select(r => r.Bpm));
        Assert.Equal(HttpStatusCode.BadRequest, (await mc.GetAsync("/api/heartrate/latest")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mc.GetAsync($"/api/heartrate/latest?astronautId={Guid.NewGuid()}")).StatusCode);
    }

    [Theory]
    [InlineData("/api/heartrate/latest")]
    [InlineData("/api/heartrate/recent")]
    public async Task Mission_control_without_vitals_view_is_403(string path)
    {
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Mc(Permissions.CrewView, Permissions.CrewManage, Permissions.VitalsHistory).GetAsync($"{path}?astronautId={_anaId}")).StatusCode);
    }

    // ----- /hubs/heartrate -----

    private Task<HttpResponseMessage> Negotiate(string query, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Hub}/negotiate?negotiateVersion=1{query}");
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return _factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task The_hub_requires_a_token_with_data_access()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Negotiate("")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Negotiate("", Token(AuthConstants.CrewAudience, _anaId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Negotiate("", Token(AuthConstants.McAudience, Guid.NewGuid(), Permissions.VitalsView))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Negotiate("", Token(AuthConstants.McAudience, Guid.NewGuid(), Permissions.CrewManage))).StatusCode);
    }

    [Fact]
    public async Task The_hub_accepts_the_token_in_the_query_string_for_websockets()
    {
        var token = Uri.EscapeDataString(Token(AuthConstants.CrewAudience, _anaId));

        Assert.Equal(HttpStatusCode.OK, (await Negotiate($"&access_token={token}")).StatusCode);
    }

    [Fact]
    public async Task The_query_token_is_not_accepted_outside_the_hub()
    {
        var token = Uri.EscapeDataString(Token(AuthConstants.CrewAudience, _anaId));

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync($"/api/heartrate/latest?access_token={token}")).StatusCode);
    }

    private HubConnection Connect(string token, string query, List<HeartRateReading> received)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{Hub}{query}", options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        connection.On<HeartRateReading>("HeartRate", r =>
        {
            lock (received)
            {
                received.Add(r);
            }
        });
        return connection;
    }

    private static int Count(List<HeartRateReading> readings)
    {
        lock (readings)
        {
            return readings.Count;
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int milliseconds = 3000)
    {
        for (var waited = 0; waited < milliseconds; waited += 50)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    [Fact]
    public async Task The_hub_sends_each_reading_only_to_the_astronauts_group()
    {
        var anaKey = await RegisterAsync(_anaId, "w-ana");
        List<HeartRateReading> ana = [], ben = [], mcAna = [];
        await using var anaConnection = Connect(Token(AuthConstants.CrewAudience, _anaId), "", ana);
        await using var benConnection = Connect(Token(AuthConstants.CrewAudience, _benId), "", ben);
        await using var mcConnection = Connect(
            Token(AuthConstants.McAudience, Guid.NewGuid(), Permissions.VitalsView), $"?astronautId={_anaId}", mcAna);
        await anaConnection.StartAsync();
        await benConnection.StartAsync();
        await mcConnection.StartAsync();

        // StartAsync returns after the handshake; joining the group happens right after, so keep sending until both arrive.
        for (var attempt = 0; attempt < 30 && !(Count(ana) > 0 && Count(mcAna) > 0); attempt++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PostReadingAsync(anaKey, 88)).StatusCode);
            await Task.Delay(100);
        }

        Assert.True(Count(ana) > 0 && Count(mcAna) > 0, $"received ana={Count(ana)}, mc={Count(mcAna)}, ben={Count(ben)}");
        Assert.All(ana, r => Assert.Equal(88, r.Bpm));
        Assert.All(mcAna, r => Assert.Equal(88, r.Bpm));
        await Task.Delay(300);
        Assert.Empty(ben);
    }

    // SignalR completes the handshake before OnConnectedAsync runs, so a refused connection may start and then be closed.
    private static async Task<bool> IsRefusedAsync(HubConnection connection)
    {
        try
        {
            await connection.StartAsync();
        }
        catch (Exception)
        {
            return true;
        }

        return await WaitForAsync(() => connection.State == HubConnectionState.Disconnected);
    }

    [Fact]
    public async Task The_hub_refuses_a_crew_member_asking_for_another_astronaut_and_mc_without_one()
    {
        List<HeartRateReading> received = [];

        await using var crew = Connect(Token(AuthConstants.CrewAudience, _anaId), $"?astronautId={_benId}", received);
        Assert.True(await IsRefusedAsync(crew));

        await using var mcWithoutAstronaut = Connect(Token(AuthConstants.McAudience, Guid.NewGuid(), Permissions.VitalsView), "", received);
        Assert.True(await IsRefusedAsync(mcWithoutAstronaut));

        await using var mcUnknown = Connect(
            Token(AuthConstants.McAudience, Guid.NewGuid(), Permissions.VitalsView), $"?astronautId={Guid.NewGuid()}", received);
        Assert.True(await IsRefusedAsync(mcUnknown));

        await using var allowed = Connect(Token(AuthConstants.CrewAudience, _anaId), $"?astronautId={_anaId}", received);
        await allowed.StartAsync();
        await Task.Delay(300);
        Assert.Equal(HubConnectionState.Connected, allowed.State);
    }

    // ----- /api/salud: per-astronaut WatchSync account -----

    [Fact]
    public async Task Salud_requires_a_token_with_data_access()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/salud/estado")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Mc(Permissions.CrewView).GetAsync("/api/salud/estado")).StatusCode);
        Assert.Empty(_factory.WatchSync.Requested);
    }

    [Fact]
    public async Task Salud_uses_the_account_of_the_astronaut_in_the_token()
    {
        Assert.Equal(HttpStatusCode.OK, (await Crew(_anaId).GetAsync("/api/salud/estado")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Crew(_anaId).GetAsync($"/api/salud/estado?astronautId={_benId}")).StatusCode);

        Assert.Equal(["ana"], _factory.WatchSync.Requested);
    }

    [Fact]
    public async Task Salud_lets_mission_control_pick_an_astronaut_or_use_the_default_account()
    {
        var mc = Mc(Permissions.VitalsView);

        Assert.Equal(HttpStatusCode.OK, (await mc.GetAsync($"/api/salud/estado?astronautId={_benId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mc.GetAsync("/api/salud/estado")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mc.GetAsync($"/api/salud/estado?astronautId={Guid.NewGuid()}")).StatusCode);

        Assert.Equal(["ben", null], _factory.WatchSync.Requested);
    }

    [Fact]
    public void Each_astronaut_maps_to_their_own_account_and_the_rest_use_the_default()
    {
        var clients = new WatchSyncClients(new WatchSyncOptions
        {
            Email = "default@example.com",
            Password = "x",
            Accounts = new(StringComparer.OrdinalIgnoreCase)
            {
                ["ana"] = new WatchSyncAccount { Email = "ana@example.com", Password = "a" },
                ["ben"] = new WatchSyncAccount { Email = "ben@example.com", Password = "b" },
                ["empty"] = new WatchSyncAccount { Email = " ", Password = "" },
            },
        });

        var ana = clients.For("ana");
        Assert.Same(ana, clients.For("ANA"));
        Assert.NotSame(ana, clients.For("ben"));

        var fallback = clients.For(null);
        Assert.Same(fallback, clients.For("kenji"));
        Assert.Same(fallback, clients.For("empty"));
        Assert.NotSame(fallback, ana);
        clients.Dispose();
    }
}
