using Heartbeat.Shared;

namespace Heartbeat.Client;

public sealed class FakeCrewApiClient : ICrewApiClient
{
    private string? _accessToken;
    private string? _refreshToken;
    private string? _deviceId;
    private AstronautProfile? _profile;

    public string? AccessToken => _accessToken;

    public string? RefreshToken => _refreshToken;

    public string? DeviceId => _deviceId;

    public bool IsOnline => true;

#pragma warning disable CS0067
    public event EventHandler<ConnectivityChangedEventArgs>? ConnectivityChanged;
#pragma warning restore CS0067

    public Task<CrewLoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        _deviceId = request.DeviceId;
        _accessToken = "fake-access-token";
        _refreshToken = "fake-refresh-token";

        _profile = new AstronautProfile(
            Guid.NewGuid(),
            request.UserName,
            request.UserName,
            "Demo",
            new MissionInfo(Guid.NewGuid(), "Artemis Demo 1", "station", DateTimeOffset.UtcNow.AddDays(-100)),
            new DateOnly(1988, 3, 12),
            null,
            DateTimeOffset.UtcNow);

        var response = new CrewLoginResponse(
            _accessToken,
            DateTimeOffset.UtcNow.AddHours(1),
            _refreshToken,
            DateTimeOffset.UtcNow.AddDays(14),
            _profile);

        return Task.FromResult(response);
    }

    public Task<TokenResponse> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_refreshToken is null)
        {
            throw new InvalidOperationException("No refresh token is available.");
        }

        _accessToken = "fake-access-token-refreshed";
        var response = new TokenResponse(
            _accessToken,
            DateTimeOffset.UtcNow.AddHours(1),
            _refreshToken,
            DateTimeOffset.UtcNow.AddDays(14));

        return Task.FromResult(response);
    }

    public Task<AstronautProfile> GetMeAsync(CancellationToken cancellationToken = default)
    {
        if (_profile is null)
        {
            throw new InvalidOperationException("Not logged in.");
        }

        return Task.FromResult(_profile);
    }

    public Task<string> GetReferenceRangesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult("{\"version\":\"fake\"}");
    }

    public Task<SyncResponse> SyncAsync(SyncRequest request, CancellationToken cancellationToken = default)
    {
        var accepted = request.CheckUps.Select(c => c.Id).Concat(request.Messages.Select(m => m.Id)).ToList();
        var response = new SyncResponse(
            DateTimeOffset.UtcNow,
            accepted,
            Array.Empty<SyncRejected>(),
            _profile,
            new SyncInbound(Array.Empty<MessageDto>(), Array.Empty<AcknowledgedAlert>()),
            false);

        return Task.FromResult(response);
    }

    public Task<Paged<CheckUpDto>> GetCheckUpsAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var response = new Paged<CheckUpDto>(Array.Empty<CheckUpDto>(), page, pageSize, 0);
        return Task.FromResult(response);
    }

    public void SetTokens(string accessToken, string refreshToken, string deviceId)
    {
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        _deviceId = deviceId;
    }

    public void Dispose()
    {
    }
}