namespace Heartbeat.Api.Data.Entities;

public class Mission
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Environment { get; set; } = "station";

    // Environment profile (HB-BE-01): gravity (g), distance from Earth (km),
    // crew size and habitable volume (m^3). Simulated demo data.
    public string EnvironmentType { get; set; } = "station";
    public decimal GravityG { get; set; }
    public decimal DistanceKm { get; set; }
    public int CrewSize { get; set; }
    public decimal HabitableVolumeM3 { get; set; }

    public DateTimeOffset StartsAt { get; set; }

    public ICollection<Astronaut> Astronauts { get; set; } = new List<Astronaut>();
}