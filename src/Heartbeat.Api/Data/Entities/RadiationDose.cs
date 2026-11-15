namespace Heartbeat.Api.Data.Entities;

public class RadiationDose
{
    public Guid Id { get; set; }
    public Guid AstronautId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public decimal DoseMsv { get; set; }
    public string? Note { get; set; }

    public Astronaut Astronaut { get; set; } = null!;
}
