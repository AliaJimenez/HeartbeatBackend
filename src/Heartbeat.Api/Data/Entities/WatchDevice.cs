namespace Heartbeat.Api.Data.Entities;

/// <summary>
/// Un reloj registrado para un astronauta (WR-BIND). La clave que presenta el reloj nunca se guarda: solo su hash SHA-256.
/// </summary>
public class WatchDevice
{
    public const int DeviceIdMaxLength = 100;
    public const int LabelMaxLength = 100;

    public Guid Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public Guid AstronautId { get; set; }
    public string? Label { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Astronaut Astronaut { get; set; } = null!;
}
