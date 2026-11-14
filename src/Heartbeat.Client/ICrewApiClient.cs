using Heartbeat.Shared;

namespace Heartbeat.Client;

public interface ICrewApiClient : IDisposable
{
    string? AccessToken { get; }

    string? RefreshToken { get; }

    string? DeviceId { get; }

    bool IsOnline { get; }

    event EventHandler<ConnectivityChangedEventArgs>? ConnectivityChanged;

    Task<CrewLoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    Task<TokenResponse> RefreshAsync(CancellationToken cancellationToken = default);

    Task<AstronautProfile> GetMeAsync(CancellationToken cancellationToken = default);

    Task<string> GetReferenceRangesAsync(CancellationToken cancellationToken = default);

    Task<SyncResponse> SyncAsync(SyncRequest request, CancellationToken cancellationToken = default);

    Task<Paged<CheckUpDto>> GetCheckUpsAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default);

    void SetTokens(string accessToken, string refreshToken, string deviceId);
}