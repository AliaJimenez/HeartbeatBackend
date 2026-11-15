using Heartbeat.Shared;

namespace Heartbeat.Api.Data.Entities;

public class RadiationDose
{
    public RadiationType Type { get; set; } = RadiationType.Gcr;

    public Guid Id { get; set; }
    public Guid AstronautId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public decimal DoseMsv { get; set; }
    public string? Note { get; set; }

    public Astronaut Astronaut { get; set; } = null!;
}