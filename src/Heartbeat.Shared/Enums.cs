namespace Heartbeat.Shared;

public enum IndicatorType
{
    BloodPressure,
    HeartRate,
    BloodSugar,
    BodyTemperature,
    Sleep,
    Mood
}

public enum HealthStatus
{
    Normal = 0,
    Watch = 1,
    Critical = 2
}

public enum Hazard
{
    SpaceRadiation,
    IsolationAndConfinement,
    DistanceFromEarth,
    GravityFields,
    HostileClosedEnvironment
}

public enum EscalationContact
{
    Self,
    CrewMedicalOfficer
}

public enum ReadingSource
{
    Manual,
    Wearable
}

public enum MessageDirection
{
    ToMissionControl,
    ToAstronaut
}

public enum MessageKind
{
    Message,
    Report
}

public enum RadiationType
{
    Gcr,
    Spe,
    Trapped
}

public enum CatalogStatus
{
    Verified,
    Heuristic
}