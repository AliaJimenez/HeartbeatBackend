using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using Heartbeat.Shared;

namespace Heartbeat.Client;

public sealed class CrewApiClient : ICrewApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ConnectivityMonitor _connectivity;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;
    private string? _refreshToken;
    private string? _deviceId;

    public CrewApiClient(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _connectivity = new ConnectivityMonitor();
        _connectivity.ConnectivityChanged += (_, e) => ConnectivityChanged?.Invoke(this, e);
    }

    public string? AccessToken => _accessToken;

    public string? RefreshToken => _refreshToken;

    public string? DeviceId => _deviceId;

    public bool IsOnline => _connectivity.IsOnline;

    public event EventHandler<ConnectivityChangedEventArgs>? ConnectivityChanged;

    public void SetTokens(string accessToken, string refreshToken, string deviceId)
    {
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        _deviceId = deviceId;
    }

    public async Task<CrewLoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Post, "api/crew/v1/auth/login", request, authenticated: false, cancellationToken);
        var result = await ReadAsync<CrewLoginResponse>(response, cancellationToken);
        SetTokens(result.AccessToken, result.RefreshToken, request.DeviceId);
        return result;
    }

    public async Task<TokenResponse> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_refreshToken is null)
        {
            throw new InvalidOperationException("No refresh token is available.");
        }

        var request = new RefreshRequest(_refreshToken, _deviceId);
        var response = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "api/crew/v1/auth/refresh")
        {
            Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json"),
        }, cancellationToken);

        response.EnsureSuccessStatusCode();
        var token = await ReadAsync<TokenResponse>(response, cancellationToken);
        _accessToken = token.AccessToken;
        if (token.RefreshToken is not null)
        {
            _refreshToken = token.RefreshToken;
        }

        return token;
    }

    public async Task<AstronautProfile> GetMeAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Get, "api/crew/v1/me", null, authenticated: true, cancellationToken);
        return await ReadAsync<AstronautProfile>(response, cancellationToken);
    }

    public async Task<string> GetReferenceRangesAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Get, "api/crew/v1/reference-ranges", null, authenticated: true, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<SyncResponse> SyncAsync(SyncRequest request, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Post, "api/crew/v1/sync", request, authenticated: true, cancellationToken);
        return await ReadAsync<SyncResponse>(response, cancellationToken);
    }

    public async Task<Paged<CheckUpDto>> GetCheckUpsAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var query = $"api/crew/v1/checkups?page={page}&pageSize={pageSize}";
        if (from.HasValue)
        {
            query += $"&from={Uri.EscapeDataString(from.Value.ToString("O"))}";
        }

        if (to.HasValue)
        {
            query += $"&to={Uri.EscapeDataString(to.Value.ToString("O"))}";
        }

        var response = await SendAsync(HttpMethod.Get, query, null, authenticated: true, cancellationToken);
        return await ReadAsync<Paged<CheckUpDto>>(response, cancellationToken);
    }

    public void Dispose()
    {
        _connectivity.Dispose();
        _refreshLock.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string uri,
        object? body,
        bool authenticated,
        CancellationToken cancellationToken)
    {
        var request = CreateRequest(method, uri, body);
        if (authenticated && _accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        }

        var response = await _http.SendAsync(request, cancellationToken);

        if (authenticated && response.StatusCode == HttpStatusCode.Unauthorized && _refreshToken is not null)
        {
            response.Dispose();
            if (await TryRefreshAsync(cancellationToken))
            {
                var retry = CreateRequest(method, uri, body);
                retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                response = await _http.SendAsync(retry, cancellationToken);
            }
            else
            {
                // Re-send is not possible without a token; return the original 401.
                request = CreateRequest(method, uri, body);
                response = await _http.SendAsync(request, cancellationToken);
            }
        }

        return response;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string uri, object? body)
    {
        var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
        }

        return request;
    }

    private async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            await RefreshAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        return result ?? throw new InvalidOperationException("The server returned an empty response.");
    }
}