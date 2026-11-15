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
    IReadOnlyList<string> BandIds,
    ReadingSource Source = ReadingSource.Manual,
    string? DeviceId = null);

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
    IReadOnlyList<MessageDto> Messages,
    IReadOnlyList<RadiationDoseDto>? RadiationDoses = null);

public sealed record SyncResponse(
    DateTimeOffset ServerTime,
    IReadOnlyList<Guid> Accepted,
    IReadOnlyList<SyncRejected> Rejected,
    AstronautProfile? Profile,
    SyncInbound Inbound,
    bool RangesUpdateAvailable,
    decimal? RadiationTotalMsv = null);

public sealed record RadiationDoseDto(
    Guid Id,
    DateTimeOffset RecordedAt,
    DateTimeOffset? ReceivedAt,
    decimal DoseMsv,
    string? Note);

public sealed record RadiationSummaryDto(
    decimal TotalMsv,
    int Entries,
    IReadOnlyList<RadiationDoseDto> Doses,
    SourceLink Source);

public sealed record Paged<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record RoleDto(
    Guid Id,
    string Name,
    bool IsSystem,
    int UserCount,
    IReadOnlyList<string> Permissions);

public sealed record PortalUserDto(
    Guid Id,
    string UserName,
    string Email,
    string DisplayName,
    RoleDto Role,
    bool IsActive,
    IReadOnlyList<string> Permissions);

public sealed record McLoginResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    PortalUserDto User);

/// <summary>One card per astronaut on the Mission Control overview (NR-08).
/// OverallStatus and LastCheckUpAt are null until the astronaut has synced a check-up.</summary>
public sealed record CrewSummaryDto(
    Guid AstronautId,
    string DisplayName,
    string? CallSign,
    HealthStatus? OverallStatus,
    DateTimeOffset? LastCheckUpAt,
    DateTimeOffset? LastSyncAt,
    int OpenAlerts,
    int UnreadMessages);

public sealed record CreateAstronautRequest(
    string UserName,
    string DisplayName,
    string? CallSign,
    Guid MissionId,
    DateOnly? DateOfBirth,
    string? Notes,
    string Password);

public sealed record UpdateAstronautRequest(
    string DisplayName,
    string? CallSign,
    Guid MissionId,
    DateOnly? DateOfBirth,
    string? Notes);

public sealed record ResetPasswordRequest(string NewPassword);


/// <summary>Latest value of one indicator for the dashboard (AstronautStatusDto).</summary>
public sealed record IndicatorStatusDto(
    IndicatorType Indicator,
    decimal Value,
    decimal? SecondaryValue,
    string Unit,
    HealthStatus Status,
    string Label,
    DateTimeOffset RecordedAt,
    Guid CheckUpId);

/// <summary>OverallStatus is null until the astronaut has synced a check-up.</summary>
public sealed record AstronautStatusDto(
    Guid AstronautId,
    HealthStatus? OverallStatus,
    DateTimeOffset? LastSyncAt,
    IReadOnlyList<IndicatorStatusDto> Indicators);

/// <summary>A shaded range of a chart: Min &lt;= value &lt; Max. A missing Min or Max means no limit.</summary>
public sealed record SeriesBandDto(HealthStatus Status, decimal? Min, decimal? Max);

/// <summary>One chart point. T is the check-up's RecordedAt.</summary>
public sealed record SeriesPointDto(DateTimeOffset T, decimal Value, decimal? SecondaryValue, HealthStatus Status);

/// <summary>Trend of one indicator, oldest point first. Bands are those of the primary value
/// (systolic for blood pressure); points of replaced check-ups are excluded.</summary>
public sealed record SeriesDto(
    IndicatorType Indicator,
    string Unit,
    IReadOnlyList<SeriesBandDto> Bands,
    IReadOnlyList<SeriesPointDto> Points);

/// <summary>CheckUpDto fields plus the evaluation (label, guidance, escalation, sources) of each reading.</summary>
public sealed record CheckUpDetailDto(
    Guid Id,
    DateTimeOffset RecordedAt,
    DateTimeOffset? ReceivedAt,
    HealthStatus OverallStatus,
    string RangesVersion,
    string? Note,
    Guid? SupersedesId,
    IReadOnlyList<ReadingDto> Readings,
    IReadOnlyList<ReadingEvaluation> Evaluations);


/// <summary>One tick box in the role checklist (GET /permissions).</summary>
public sealed record PermissionDto(string Key, string Group, string Description);

/// <summary>Body of POST /roles and PUT /roles/{id}.</summary>
public sealed record SaveRoleRequest(string Name, IReadOnlyList<string> Permissions);


/// <summary>Body of POST /users.</summary>
public sealed record CreateUserRequest(
    string UserName,
    string Email,
    string DisplayName,
    string Password,
    Guid RoleId);

/// <summary>Body of PUT /users/{id}. UserName and role are changed elsewhere.</summary>
public sealed record UpdateUserRequest(
    string Email,
    string DisplayName);

/// <summary>Body of PUT /users/{id}/role.</summary>
public sealed record SetUserRoleRequest(Guid RoleId);


/// <summary>A critical-result alert as shown in Mission Control (GET /alerts).</summary>
public sealed record AlertDto(
    Guid Id,
    Guid AstronautId,
    string AstronautName,
    Guid CheckUpId,
    IReadOnlyList<IndicatorType> Indicators,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AcknowledgedAt,
    string? AcknowledgedBy,
    string? AcknowledgeNote);

/// <summary>Body of POST /alerts/{id}/acknowledge.</summary>
public sealed record AcknowledgeAlertRequest(string Note);


/// <summary>Body of POST /crew/{id}/messages (Mission Control reply).</summary>
public sealed record SendMessageRequest(string Subject, string Body);
