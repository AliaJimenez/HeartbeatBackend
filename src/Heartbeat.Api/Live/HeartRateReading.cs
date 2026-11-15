using System.Text.Json.Serialization;

namespace Heartbeat.Api.Live;

/// <summary>Lectura de frecuencia cardíaca que envía el Galaxy Watch 5 cada segundo (contrato fijo del reloj).</summary>
public sealed record HeartRateReading(
    [property: JsonPropertyName("bpm")] int Bpm,
    [property: JsonPropertyName("measuredAt")] DateTimeOffset MeasuredAt,
    [property: JsonPropertyName("source")] string? Source);
