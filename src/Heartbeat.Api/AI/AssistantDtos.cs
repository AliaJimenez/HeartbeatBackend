using Heartbeat.Shared;

namespace Heartbeat.Api.AI;

public sealed record AssistantRequest(string? Question, string? Language = "es");

public sealed record AssistantResponse(
    string Answer,
    DateTimeOffset GeneratedAt,
    string Provider,
    string Model,
    IReadOnlyList<string> SuggestedQuestions,
    string Disclaimer);

public sealed record AssistantAlertContext(
    Guid Id,
    AlertKind Kind,
    string Indicators,
    string? Message,
    DateTimeOffset CreatedAt);

/// <summary>Datos mínimos y autorizados que se entregan a Claude. No incluye notas, mensajes ni fecha de nacimiento.</summary>
public sealed record AssistantContext(
    Guid AstronautId,
    string DisplayName,
    string MissionName,
    DateTimeOffset GeneratedAt,
    RiskStatus Risk,
    IReadOnlyList<AssistantAlertContext> OpenAlerts,
    bool ContainsSimulatedData = true);

public sealed record AssistantProviderResult(string Answer, string Provider, string Model);
