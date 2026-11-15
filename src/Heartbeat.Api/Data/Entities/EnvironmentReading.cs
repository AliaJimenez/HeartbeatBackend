using Heartbeat.Shared;

namespace Heartbeat.Api.Data.Entities;

/// <summary>
/// One environment reading (astronaut- or habitat-level): indicator id from the catalog,
/// value, timestamp. All data is simulated (HB-BE-01).
/// </summary>
public class EnvironmentReading
{
    public Guid Id { get; set; }

    // Either an astronaut reading or a habitat/mission reading (at least one should be set).
    public Guid? AstronautId { get; set; }
    public Guid? MissionId { get; set; }

    public string IndicatorId { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public decimal? SecondaryValue { get; set; }
    public string? Unit { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }

    public bool Simulated { get; set; } = true;
    public string? Note { get; set; }

    public Astronaut? Astronaut { get; set; }
    public Mission? Mission { get; set; }
}