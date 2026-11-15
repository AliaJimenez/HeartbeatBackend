using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Heartbeat.Api.Salud;

/// <summary>
/// Cliente del backend Python "WatchSync API". La sesión es una cookie (<c>session</c>) que guarda el
/// <see cref="CookieContainer"/> singleton; si una llamada devuelve 401 se hace login y se repite una sola vez.
/// </summary>
public sealed class WatchSyncClient
{
    private const string Unavailable = "El servidor de datos de Samsung Health no responde";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // El cliente tipado es transitorio: el semáforo es estático para que solo haya un login a la vez en todo el proceso.
    private static readonly SemaphoreSlim LoginLock = new(1, 1);

    private readonly HttpClient _http;
    private readonly WatchSyncOptions _options;

    public WatchSyncClient(HttpClient http, IOptions<WatchSyncOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public Task<DailySummaryDto> GetSummaryAsync(DateOnly date, CancellationToken ct) =>
        GetAsync<DailySummaryDto>($"summary?date={Day(date)}", ct);

    public Task<SeriesDto> GetHeartRateAsync(DateTimeOffset from, DateTimeOffset to, string resolution, CancellationToken ct) =>
        GetAsync<SeriesDto>($"metrics/heart_rate?from={Iso(from)}&to={Iso(to)}&resolution={Uri.EscapeDataString(resolution)}", ct);

    public Task<List<SleepRecordDto>> GetSleepAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        GetAsync<List<SleepRecordDto>>($"sleep?from={Iso(from)}&to={Iso(to)}", ct);

    public Task<List<DailyActivityDto>> GetDailyActivityAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        GetAsync<List<DailyActivityDto>>($"activity/daily?from={Day(from)}&to={Day(to)}", ct);

    public Task<List<BloodPressureDto>> GetBloodPressureAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        GetAsync<List<BloodPressureDto>>($"blood-pressure?from={Iso(from)}&to={Iso(to)}", ct);

    public Task<List<SyncStatusDto>> GetSyncStatusAsync(CancellationToken ct) =>
        GetAsync<List<SyncStatusDto>>("sync/status", ct);

    // ISO-8601 con desfase; el signo + del desfase debe viajar como %2B.
    private static string Iso(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O"));

    private static string Day(DateOnly value) => value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<T> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, relativeUrl), ct);
        await EnsureSuccessAsync(response, ct);
        T? body = await response.Content.ReadFromJsonAsync<T>(Json, ct);
        return body ?? throw new WatchSyncApiException(StatusCodes.Status502BadGateway, null, "Respuesta vacía del servidor de datos");
    }

    /// <summary>Envía la petición; si responde 401, hace login y la repite UNA vez con una petición nueva.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> crear, CancellationToken ct)
    {
        HttpResponseMessage response = await SendOnceAsync(crear(), ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        await LoginAsync(ct);
        return await SendOnceAsync(crear(), ct);
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        await LoginLock.WaitAsync(ct);
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "auth/login")
            {
                Content = JsonContent.Create(new LoginBody(_options.Email, _options.Password), options: Json),
            };
            using HttpResponseMessage response = await SendOnceAsync(request, ct);
            await EnsureSuccessAsync(response, ct);
        }
        finally
        {
            LoginLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            try
            {
                return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new WatchSyncUnavailableException(Unavailable, ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new WatchSyncUnavailableException(Unavailable, ex);
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? code = null;
        string message = $"El servidor de datos respondió {(int)response.StatusCode}";
        try
        {
            ErrorBody? error = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, ct);
            code = error?.Code;
            if (!string.IsNullOrWhiteSpace(error?.Message))
            {
                message = error.Message;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // El cuerpo no es el JSON de error esperado: se conserva el mensaje genérico.
        }

        throw new WatchSyncApiException((int)response.StatusCode, code, message);
    }

    private sealed record LoginBody(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("password")] string Password);

    private sealed record ErrorBody(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("message")] string? Message);
}
