namespace Heartbeat.Api.Data.Entities;

public class Mission
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Environment { get; set; } = "station";
    public DateTimeOffset StartsAt { get; set; }

    public ICollection<Astronaut> Astronauts { get; set; } = new List<Astronaut>();
}