using System.Text.Json;

namespace Heartbeat.Shared;

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

    public static SourceLink GetSource(string sourceId)
    {
        using var doc = Parse();
        if (!doc.RootElement.GetProperty("sources").TryGetProperty(sourceId, out var source))
        {
            throw new KeyNotFoundException($"Source '{sourceId}' is not defined in the reference ranges.");
        }

        return new SourceLink(source.GetProperty("title").GetString()!, new Uri(source.GetProperty("url").GetString()!));
    }
}