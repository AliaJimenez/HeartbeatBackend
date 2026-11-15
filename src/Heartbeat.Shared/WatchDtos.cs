namespace Heartbeat.Shared;

/// <summary>Body of POST /api/mc/v1/watches: registers a watch for an astronaut (WR-BIND).</summary>
public sealed record RegisterWatchRequest(Guid AstronautId, string DeviceId, string? Label = null);

/// <summary>A registered watch. The key is never part of it: it is only shown when created or rotated.</summary>
public sealed record WatchDeviceDto(
    Guid Id,
    string DeviceId,
    Guid AstronautId,
    string? Label,
    DateTimeOffset CreatedAt);

/// <summary>
/// Result of registering a watch or rotating its key. <see cref="Key"/> is what the watch sends in the
/// <c>X-Watch-Key</c> header; it cannot be read again, only replaced.
/// </summary>
public sealed record WatchRegisteredDto(WatchDeviceDto Device, string Key);
