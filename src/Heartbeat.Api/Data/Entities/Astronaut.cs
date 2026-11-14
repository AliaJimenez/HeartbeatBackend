using Heartbeat.Shared;

namespace Heartbeat.Api.Data.Entities;

public class Astronaut
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? CallSign { get; set; }
    public Guid MissionId { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Mission Mission { get; set; } = null!;
    public ICollection<CheckUp> CheckUps { get; set; } = new List<CheckUp>();
    public ICollection<Message> Messages { get; set; } = new List<Message>();
}