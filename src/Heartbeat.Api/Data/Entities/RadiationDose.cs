namespace Heartbeat.Api.Data.Entities;

using Heartbeat.Shared;

public class RadiationDose
{
    public Guid Id { get; set; }
    public Guid AstronautId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public decimal DoseMsv { get; set; }
    public string? Note { get; set; }

    // GCR by default (HB-BE-01).
    public RadiationType Type { get; set; } = RadiationType.Gcr;

    public Astronaut Astronaut { get; set; } = null!;
}
