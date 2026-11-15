using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;

namespace Heartbeat.Tests;

/// <summary>
/// WR-TESTS (#154): /api/salud con un WatchSync falso (HttpMessageHandler): reenvío de los datos, validación de los
/// parámetros, 503 si no responde, 400/404/422 reenviados, otro código → 502 y re-login una sola vez ante 401.
/// </summary>
public class SaludApiTests : WatchApiTestBase
{
    private readonly FakeWatchSyncServer _server;
    private readonly ScriptedWatchSyncClients _clients;

    public SaludApiTests() : this(new FakeWatchSyncServer())
    {
    }

    private SaludApiTests(FakeWatchSyncServer server) : this(server, new ScriptedWatchSyncClients(server))
    {
    }

    private SaludApiTests(FakeWatchSyncServer server, ScriptedWatchSyncClients clients) : base(clients)
    {
        _server = server;
        _clients = clients;
    }

    private Task<HttpResponseMessage> GetAsync(string path) => Crew(AnaId).GetAsync($"/api/salud/{path}");

    private static async Task<string?> ErrorOf(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetString();
    }

    private FakeWatchSyncServer.Call LastDataCall()
    {
        // La primera petición siempre se rechaza con 401 (sin sesión) y se repite tras el login: la útil es la última.
        return _server.DataCalls.Last();
    }

    private static NameValueCollection QueryOf(FakeWatchSyncServer.Call call)
        => HttpUtility.ParseQueryString(new Uri("http://x" + call.PathAndQuery).Query);

    // ----- Reenvío de los datos -----

    [Fact]
    public async Task Resumen_forwards_the_summary_and_defaults_to_today()
    {
        var res = await GetAsync("resumen");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"unacknowledged_alerts\":2", body);
        Assert.Contains("\"heart_rate\"", body);
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), QueryOf(LastDataCall())["date"]);
        Assert.StartsWith("/api/v1/summary?", LastDataCall().PathAndQuery);
    }

    [Fact]
    public async Task Resumen_uses_the_requested_date()
    {
        await GetAsync("resumen?fecha=2026-11-10");

        Assert.Equal("2026-11-10", QueryOf(LastDataCall())["date"]);
    }

    [Theory]
    [InlineData("15/11/2026")]
    [InlineData("2026-13-01")]
    [InlineData("20261115")]
    [InlineData("abc")]
    public async Task Resumen_rejects_a_bad_date_without_calling_the_backend(string fecha)
    {
        var res = await GetAsync($"resumen?fecha={Uri.EscapeDataString(fecha)}");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("yyyy-MM-dd", await ErrorOf(res));
        Assert.Empty(_server.Calls);
    }

    [Fact]
    public async Task Fc_defaults_to_one_hour_resolution_over_seven_days()
    {
        await GetAsync("fc?hasta=2026-11-15T12:00:00Z");

        var query = QueryOf(LastDataCall());
        Assert.StartsWith("/api/v1/metrics/heart_rate?", LastDataCall().PathAndQuery);
        Assert.Equal("1h", query["resolution"]);
        Assert.Equal(TimeSpan.FromDays(7), DateTimeOffset.Parse(query["to"]!) - DateTimeOffset.Parse(query["from"]!));
    }

    [Theory]
    [InlineData("raw", 24)]
    [InlineData("5m", 24 * 7)]
    [InlineData("1h", 24 * 7)]
    [InlineData("1d", 24 * 7)]
    public async Task Fc_defaults_the_range_by_resolution(string resolution, int hours)
    {
        await GetAsync($"fc?resolucion={resolution}&hasta=2026-11-15T12:00:00Z");

        var query = QueryOf(LastDataCall());
        Assert.Equal(resolution, query["resolution"]);
        Assert.Equal(TimeSpan.FromHours(hours), DateTimeOffset.Parse(query["to"]!) - DateTimeOffset.Parse(query["from"]!));
    }

    [Theory]
    [InlineData("2h")]
    [InlineData("RAW")]
    [InlineData("1w")]
    public async Task Fc_rejects_an_unknown_resolution_without_calling_the_backend(string resolution)
    {
        var res = await GetAsync($"fc?resolucion={resolution}");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("raw, 5m, 1h o 1d", await ErrorOf(res));
        Assert.Empty(_server.Calls);
    }

    [Fact]
    public async Task Fc_sends_the_dates_in_iso_8601_with_the_plus_of_the_offset_encoded()
    {
        await GetAsync("fc?desde=2026-11-14T10:00:00%2B02:00&hasta=2026-11-15T10:00:00%2B02:00");

        var call = LastDataCall();
        Assert.Contains("from=2026-11-14T10%3A00%3A00.0000000%2B02%3A00", call.PathAndQuery);
        Assert.Equal("2026-11-14T10:00:00.0000000+02:00", QueryOf(call)["from"]);
        Assert.Equal("2026-11-15T10:00:00.0000000+02:00", QueryOf(call)["to"]);
    }

    [Fact]
    public async Task Sueno_and_presion_default_to_the_last_seven_days()
    {
        await GetAsync("sueno?hasta=2026-11-15T12:00:00Z");
        var sleep = QueryOf(LastDataCall());
        await GetAsync("presion?hasta=2026-11-15T12:00:00Z");
        var pressure = QueryOf(LastDataCall());

        Assert.StartsWith("/api/v1/blood-pressure?", LastDataCall().PathAndQuery);
        Assert.Equal(TimeSpan.FromDays(7), DateTimeOffset.Parse(sleep["to"]!) - DateTimeOffset.Parse(sleep["from"]!));
        Assert.Equal(TimeSpan.FromDays(7), DateTimeOffset.Parse(pressure["to"]!) - DateTimeOffset.Parse(pressure["from"]!));
        Assert.Contains(_server.DataCalls, c => c.PathAndQuery.StartsWith("/api/v1/sleep?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Actividad_defaults_to_the_last_seven_days_inclusive_and_takes_dates_without_time()
    {
        await GetAsync("actividad?hasta=2026-11-15");
        var defaults = QueryOf(LastDataCall());
        await GetAsync("actividad?desde=2026-11-01&hasta=2026-11-03");
        var explicitRange = QueryOf(LastDataCall());

        Assert.StartsWith("/api/v1/activity/daily?", LastDataCall().PathAndQuery);
        Assert.Equal("2026-11-09", defaults["from"]);
        Assert.Equal("2026-11-15", defaults["to"]);
        Assert.Equal(("2026-11-01", "2026-11-03"), (explicitRange["from"], explicitRange["to"]));
    }

    [Fact]
    public async Task Estado_forwards_the_sync_status()
    {
        var res = await GetAsync("estado");

        var status = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single();
        Assert.Equal("heart_rate", status.GetProperty("data_type").GetString());
        Assert.Equal("/api/v1/sync/status", LastDataCall().PathAndQuery);
    }

    // ----- Errores del backend -----

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "validation_error", "fecha inválida")]
    [InlineData(HttpStatusCode.NotFound, "not_found", "no hay datos para ese día")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "bad_range", "from debe ser anterior a to")]
    public async Task A_400_404_or_422_from_the_backend_is_forwarded_with_its_message(HttpStatusCode status, string code, string message)
    {
        _server.Respond(_ => FakeWatchSyncServer.Error(status, code, message));

        var res = await GetAsync("estado");

        Assert.Equal(status, res.StatusCode);
        Assert.Equal(message, await ErrorOf(res));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Any_other_error_code_from_the_backend_becomes_502_with_its_message(HttpStatusCode status)
    {
        _server.Respond(_ => FakeWatchSyncServer.Error(status, "backend_error", "el backend falló"));

        var res = await GetAsync("estado");

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Equal("el backend falló", await ErrorOf(res));
    }

    [Fact]
    public async Task A_backend_error_without_a_message_gets_a_generic_502()
    {
        _server.Respond(_ => FakeWatchSyncServer.Json("<html>boom</html>", HttpStatusCode.InternalServerError));

        var res = await GetAsync("resumen");

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Equal("El servidor de datos respondió 500", await ErrorOf(res));
    }

    [Fact]
    public async Task A_backend_that_cannot_be_reached_is_503()
    {
        _server.RespondAsync = (_, _) => throw new HttpRequestException("connection refused");

        var res = await GetAsync("estado");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal("El servidor de datos de Samsung Health no responde", await ErrorOf(res));
    }

    [Fact]
    public async Task A_backend_that_takes_too_long_is_503()
    {
        _clients.Timeout = TimeSpan.FromMilliseconds(200);
        _server.RespondAsync = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return FakeWatchSyncServer.Json("[]");
        };

        var res = await GetAsync("estado");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal("El servidor de datos de Samsung Health no responde", await ErrorOf(res));
    }

    [Fact]
    public async Task A_login_that_cannot_be_reached_is_503()
    {
        _server.LoginAsync = _ => throw new HttpRequestException("connection refused");

        var res = await GetAsync("estado");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }

    // ----- Re-login -----

    [Fact]
    public async Task A_401_from_the_backend_logs_in_once_and_the_request_succeeds()
    {
        var res = await GetAsync("estado");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, _server.LoginCount);
        Assert.Equal(2, _server.DataCalls.Count);
    }

    [Fact]
    public async Task Later_requests_reuse_the_session()
    {
        await GetAsync("estado");
        await GetAsync("resumen");
        await GetAsync("sueno");

        Assert.Equal(1, _server.LoginCount);
        Assert.Equal(4, _server.DataCalls.Count);
    }

    [Fact]
    public async Task An_expired_session_logs_in_again_without_the_caller_noticing()
    {
        await GetAsync("estado");
        _server.ExpireSession();

        var res = await GetAsync("estado");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, _server.LoginCount);
    }

    [Fact]
    public async Task A_401_that_persists_after_login_is_502_after_a_single_login()
    {
        _server.Respond(_ => FakeWatchSyncServer.Error(HttpStatusCode.Unauthorized, "unauthorized", "Cuenta sin permiso"));

        var res = await GetAsync("estado");

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Equal("Cuenta sin permiso", await ErrorOf(res));
        Assert.Equal(1, _server.LoginCount);
        Assert.Equal(3, _server.Calls.Count);
    }

    [Fact]
    public async Task A_rejected_login_is_502_and_the_request_is_not_repeated()
    {
        _server.LoginAsync = _ => Task.FromResult(FakeWatchSyncServer.Error(HttpStatusCode.Unauthorized, "invalid_credentials", "Credenciales inválidas"));

        var res = await GetAsync("estado");

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Equal("Credenciales inválidas", await ErrorOf(res));
        Assert.Equal(["GET", "POST"], _server.Calls.Select(c => c.Method.Method));
    }
}
