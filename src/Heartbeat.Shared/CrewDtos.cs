namespace Heartbeat.Shared;

public sealed record MissionInfo(
    Guid Id,
    string Name,
    string Environment,
    DateTimeOffset StartsAt);

public sealed record AstronautProfile(
    Guid Id,
    string UserName,
    string DisplayName,
    string? CallSign,
    MissionInfo Mission,
    DateOnly? DateOfBirth,
    string? Notes,
    DateTimeOffset UpdatedAt);

public sealed record UpdateProfileRequest(
    string DisplayName,
    string? CallSign,
    string? Notes,
    DateTimeOffset UpdatedAt);

public sealed record LoginRequest(
    string UserName,
    string Password,
    string DeviceId);

public sealed record CrewLoginResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    AstronautProfile Profile);

public sealed record RefreshRequest(
    string RefreshToken,
    string? DeviceId = null);

public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string? RefreshToken = null,
    DateTimeOffset? RefreshTokenExpiresAt = null);

public sealed record ReadingDto(
    Guid Id,
    IndicatorType Indicator,
    decimal Value,
    decimal? SecondaryValue,
    HealthStatus Status,
    IReadOnlyList<string> BandIds);

public sealed record CheckUpDto(
    Guid Id,
    DateTimeOffset RecordedAt,
    DateTimeOffset? ReceivedAt,
    HealthStatus OverallStatus,
    string RangesVersion,
    string? Note,
    Guid? SupersedesId,
    IReadOnlyList<ReadingDto> Readings);

public sealed record MessageDto(
    Guid Id,
    MessageDirection Direction,
    MessageKind Kind,
    Guid? CheckUpId,
    string Subject,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? ReadAt,
    string? SenderName);

public sealed record SyncRejected(
    Guid Id,
    string Reason);

public sealed record AcknowledgedAlert(
    Guid CheckUpId,
    DateTimeOffset AcknowledgedAt,
    string By,
    string? Note);

public sealed record SyncInbound(
    IReadOnlyList<MessageDto> Messages,
    IReadOnlyList<AcknowledgedAlert> AcknowledgedAlerts);

public sealed record SyncRequest(
    string DeviceId,
    DateTimeOffset? LastPulledAt,
    string RangesVersion,
    UpdateProfileRequest? Profile,
    IReadOnlyList<CheckUpDto> CheckUps,
    IReadOnlyList<MessageDto> Messages);

public sealed record SyncResponse(
    DateTimeOffset ServerTime,
    IReadOnlyList<Guid> Accepted,
    IReadOnlyList<SyncRejected> Rejected,
    AstronautProfile? Profile,
    SyncInbound Inbound,
    bool RangesUpdateAvailable);

public sealed record Paged<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int Total);