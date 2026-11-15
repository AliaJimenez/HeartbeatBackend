using Heartbeat.Shared;

namespace Heartbeat.Api.Data.Entities;

public class Reading
{
    public const int DeviceIdMaxLength = 100;
    public const int ExternalIdMaxLength = 200;

    public Guid Id { get; set; }
    public Guid CheckUpId { get; set; }
    public IndicatorType Indicator { get; set; }
    public decimal Value { get; set; }
    public decimal? SecondaryValue { get; set; }
    public HealthStatus Status { get; set; }
    public string BandIds { get; set; } = string.Empty;
    public ReadingSource Source { get; set; } = ReadingSource.Manual;
    public string? DeviceId { get; set; }
    public string? ExternalId { get; set; }

    public CheckUp CheckUp { get; set; } = null!;
}
