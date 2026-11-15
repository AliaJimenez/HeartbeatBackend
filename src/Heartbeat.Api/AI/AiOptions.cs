namespace Heartbeat.Api.AI;

/// <summary>Configuración del asistente. La clave se carga desde user-secrets o AI__ApiKey, nunca desde Git.</summary>
public sealed class AiOptions
{
    public const string SectionName = "AI";

    public bool Enabled { get; init; }
    public string Provider { get; init; } = "Anthropic";
    public string BaseUrl { get; init; } = "https://api.anthropic.com/";
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 20;
    public int MaxOutputTokens { get; init; } = 700;

    public bool IsAnthropic => string.Equals(Provider, "Anthropic", StringComparison.OrdinalIgnoreCase);
    public bool IsConfigured => Enabled && IsAnthropic && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Model);
}
