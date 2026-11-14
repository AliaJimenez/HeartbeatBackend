using System.Text.Json;

namespace Heartbeat.Shared;

/// <summary>
/// Access to the embedded reference-ranges.json resource.
/// </summary>
public static class ReferenceRanges
{
    public const string ResourceName = "Heartbeat.Shared.reference-ranges.json";

    private static readonly Lazy<string> _json = new(() =>
    {
        using var stream = OpenStream();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static readonly Lazy<string> _version = new(() =>
    {
        using var doc = JsonDocument.Parse(_json.Value);
        return doc.RootElement.GetProperty("version").GetString() ?? "unknown";
    });

    public static string Json => _json.Value;

    public static string Version => _version.Value;

    public static Stream OpenStream()
    {
        var assembly = typeof(ReferenceRanges).Assembly;
        var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found in {assembly.FullName}.");
        }
        return stream;
    }

    public static JsonDocument Parse() => JsonDocument.Parse(Json);
}