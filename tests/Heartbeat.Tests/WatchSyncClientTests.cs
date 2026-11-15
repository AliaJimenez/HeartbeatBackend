using System.Net;
using System.Text.Json;
using Heartbeat.Api.Salud;

namespace Heartbeat.Tests;

/// <summary>WR-TESTS (#154): WatchSyncClient contra un WatchSync falso (HttpMessageHandler): errores, tiempo de espera y re-login.</summary>
public class WatchSyncClientTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

    private static Task<List<SyncStatusDto>> Status(WatchSyncClient client) => client.GetSyncStatusAsync(CancellationToken.None);

    // ----- Re-login -----

    [Fact]
    public async Task A_401_logs_in_and_repeats_the_request_once()
    {
        var server = new FakeWatchSyncServer();
        var client = server.CreateClient();

        var status = await Status(client);

        Assert.Single(status);
        Assert.Equal(1, server.LoginCount);
        Assert.Equal(
            ["GET /api/v1/sync/status", "POST /api/v1/auth/login", "GET /api/v1/sync/status"],
            server.Calls.Select(c => $"{c.Method} {c.PathAndQuery}"));
    }

    [Fact]
    public async Task The_login_sends_the_configured_account()
    {
        var server = new FakeWatchSyncServer();

        await Status(server.CreateClient());

        using var body = JsonDocument.Parse(server.Calls.Single(c => c.Method == HttpMethod.Post).Body!);
        Assert.Equal(FakeWatchSyncServer.Email, body.RootElement.GetProperty("email").GetString());
        Assert.Equal(FakeWatchSyncServer.Password, body.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task While_the_session_is_valid_there_is_no_new_login()
    {
        var server = new FakeWatchSyncServer();
        var client = server.CreateClient();

        await Status(client);
        await Status(client);
        await client.GetSummaryAsync(new DateOnly(2026, 11, 15), CancellationToken.None);

        Assert.Equal(1, server.LoginCount);
        Assert.Equal(4, server.DataCalls.Count); // 1 rechazada + 3 con sesión
    }

    [Fact]
    public async Task An_expired_session_logs_in_again_once()
    {
        var server = new FakeWatchSyncServer();
        var client = server.CreateClient();
        await Status(client);

        server.ExpireSession();
        await Status(client);

        Assert.Equal(2, server.LoginCount);
    }

    [Fact]
    public async Task A_401_that_persists_after_login_is_not_retried_again()
    {
        var server = new FakeWatchSyncServer();
        server.Respond(_ => FakeWatchSyncServer.Error(HttpStatusCode.Unauthorized, "unauthorized", "Cuenta sin permiso"));

        var error = await Assert.ThrowsAsync<WatchSyncApiException>(() => Status(server.CreateClient()));

        Assert.Equal(401, error.StatusCode);
        Assert.Equal("unauthorized", error.Code);
        Assert.Equal(1, server.LoginCount);
        Assert.Equal(3, server.Calls.Count); // GET, login, GET: nunca un segundo login
    }

    [Fact]
    public async Task A_rejected_login_fails_and_the_request_is_not_repeated()
    {
        var server = new FakeWatchSyncServer();
        var client = new WatchSyncClient(
            new HttpClient(server) { BaseAddress = FakeWatchSyncServer.BaseAddress }, FakeWatchSyncServer.Email, "wrong-password");

        var error = await Assert.ThrowsAsync<WatchSyncApiException>(() => Status(client));

        Assert.Equal(401, error.StatusCode);
        Assert.Equal("invalid_credentials", error.Code);
        Assert.Equal(["GET", "POST"], server.Calls.Select(c => c.Method.Method));
    }

    [Fact]
    public async Task Only_a_401_triggers_a_login()
    {
        var server = new FakeWatchSyncServer();
        var client = server.CreateClient();
        await Status(client); // abre la sesión: un login
        server.Respond(_ => FakeWatchSyncServer.Error(HttpStatusCode.InternalServerError, "boom", "falló"));

        await Assert.ThrowsAsync<WatchSyncApiException>(() => Status(client));

        Assert.Equal(1, server.LoginCount);
    }

    // ----- Errores -----

    [Fact]
    public async Task A_backend_that_cannot_be_reached_is_unavailable()
    {
        var server = new FakeWatchSyncServer();
        server.RespondAsync = (_, _) => throw new HttpRequestException("connection refused");

        var error = await Assert.ThrowsAsync<WatchSyncUnavailableException>(() => Status(server.CreateClient()));

        Assert.Contains("no responde", error.Message);
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Fact]
    public async Task A_login_that_cannot_be_reached_is_unavailable()
    {
        var server = new FakeWatchSyncServer { LoginAsync = _ => throw new HttpRequestException("connection refused") };

        await Assert.ThrowsAsync<WatchSyncUnavailableException>(() => Status(server.CreateClient()));
    }

    [Fact]
    public async Task A_backend_that_takes_too_long_is_unavailable()
    {
        var server = new FakeWatchSyncServer();
        server.RespondAsync = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return FakeWatchSyncServer.Json("[]");
        };

        var error = await Assert.ThrowsAsync<WatchSyncUnavailableException>(
            () => Status(server.CreateClient(TimeSpan.FromMilliseconds(150))));

        Assert.IsAssignableFrom<TaskCanceledException>(error.InnerException);
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_reported_as_unavailable()
    {
        var server = new FakeWatchSyncServer();
        server.RespondAsync = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return FakeWatchSyncServer.Json("[]");
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.CreateClient().GetSyncStatusAsync(cts.Token));
    }

    [Fact]
    public async Task An_error_exposes_the_status_code_the_code_and_the_message_of_the_backend()
    {
        var server = new FakeWatchSyncServer();
        server.Respond(_ => FakeWatchSyncServer.Error(HttpStatusCode.UnprocessableEntity, "bad_range", "from debe ser anterior a to"));

        var error = await Assert.ThrowsAsync<WatchSyncApiException>(() => server.CreateClient().GetSleepAsync(From, To, CancellationToken.None));

        Assert.Equal(422, error.StatusCode);
        Assert.Equal("bad_range", error.Code);
        Assert.Equal("from debe ser anterior a to", error.Message);
    }

    [Theory]
    [InlineData("boom")]
    [InlineData("{\"code\":\"x\"}")]
    [InlineData("{\"message\":\"   \"}")]
    public async Task An_error_body_without_a_usable_message_gets_a_generic_one(string body)
    {
        var server = new FakeWatchSyncServer();
        server.Respond(_ => FakeWatchSyncServer.Json(body, HttpStatusCode.InternalServerError));

        var error = await Assert.ThrowsAsync<WatchSyncApiException>(() => Status(server.CreateClient()));

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("El servidor de datos respondió 500", error.Message);
    }

    [Fact]
    public async Task An_empty_success_body_is_a_bad_gateway_error()
    {
        var server = new FakeWatchSyncServer();
        server.Respond(_ => FakeWatchSyncServer.Json("null"));

        var error = await Assert.ThrowsAsync<WatchSyncApiException>(() => Status(server.CreateClient()));

        Assert.Equal(502, error.StatusCode);
    }

    // ----- Contrato -----

    [Fact]
    public async Task The_summary_is_read_from_the_snake_case_contract_tolerating_missing_fields()
    {
        var server = new FakeWatchSyncServer();

        var summary = await server.CreateClient().GetSummaryAsync(new DateOnly(2026, 11, 15), CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 11, 15), summary.Date);
        Assert.Equal(72, summary.Latest.HeartRate!.Value);
        Assert.Equal("lpm", summary.Latest.HeartRate.Unit);
        Assert.Equal(2, summary.UnacknowledgedAlerts);
        Assert.Null(summary.Sleep);
        Assert.Null(summary.Activity);
        Assert.Null(summary.Latest.BloodPressure);
    }
}
