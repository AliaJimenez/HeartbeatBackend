using System.Text.Json;

namespace Heartbeat.Api.Vitals;

/// <summary>
/// Read-only view of the (simulated) indicators catalog (contracts/indicators.catalog.json).
/// Loaded alongside <see cref="RangesCatalog"/> (HB-BE-01). The catalog is populated by HB-DA-01;
/// until then this stays empty and harmless.
/// </summary>
public sealed class IndicatorsCatalog
{
    public string Version { get; }

    public IndicatorsCatalog() : this(null) { }

    public IndicatorsCatalog(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            Version = "unknown";
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            Version = doc.RootElement.TryGetProperty("version", out var v) ? (v.GetString() ?? "unknown") : "unknown";
        }
        catch
        {
            Version = "unknown";
        }
    }
}