using Heartbeat.Shared;

namespace Heartbeat.Api.Data.Entities;

public class Alert
{
    public Guid Id { get; set; }
    public Guid AstronautId { get; set; }
    public Guid? CheckUpId { get; set; }
    public AlertKind Kind { get; set; } = AlertKind.CheckUp;
    public Guid? RadiationDoseId { get; set; }
    public string? Message { get; set; }
    public string Indicators { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
    public string? AcknowledgeNote { get; set; }

    public Astronaut Astronaut { get; set; } = null!;
    public CheckUp? CheckUp { get; set; }
}