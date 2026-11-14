using Heartbeat.Shared;

namespace Heartbeat.Api.Data.Entities;

public class CheckUp
{
    public Guid Id { get; set; }
    public Guid AstronautId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public HealthStatus OverallStatus { get; set; }
    public string RangesVersion { get; set; } = string.Empty;
    public string? Note { get; set; }
    public Guid? SupersedesId { get; set; }

    public Astronaut Astronaut { get; set; } = null!;
    public ICollection<Reading> Readings { get; set; } = new List<Reading>();
    public Alert? Alert { get; set; }
}