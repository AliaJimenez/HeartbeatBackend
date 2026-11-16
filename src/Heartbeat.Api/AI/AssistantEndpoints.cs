using System.Security.Claims;
using Heartbeat.Api.Auth;
using Heartbeat.Shared;
using Microsoft.Extensions.Options;

namespace Heartbeat.Api.AI;

public static class AssistantEndpoints
{
    public const int MaxQuestionLength = 1_000;

    public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/crew/v1/assistant")
            .RequireAuthorization(AuthConstants.CrewPolicy)
            .MapPost("/chat", ChatForCrewAsync);

        app.MapPost("/api/mc/v1/crew/{astronautId:guid}/assistant/chat", ChatForMissionControlAsync)
            .RequirePermission(Permissions.VitalsView);
        return app;
    }

    private static async Task<IResult> ChatForCrewAsync(
        AssistantRequest request,
        ClaimsPrincipal caller,
        IOptions<AiOptions> options,
        AssistantContextBuilder contexts,
        IAssistantProvider provider,
        CancellationToken cancellationToken)
    {
        string? subject = caller.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out Guid astronautId))
        {
            return Results.Problem(title: "Invalid token", statusCode: StatusCodes.Status401Unauthorized);
        }

        return await ChatAsync(request, astronautId, options, contexts, provider, cancellationToken);
    }

    private static Task<IResult> ChatForMissionControlAsync(
        Guid astronautId,
        AssistantRequest request,
        IOptions<AiOptions> options,
        AssistantContextBuilder contexts,
        IAssistantProvider provider,
        CancellationToken cancellationToken) =>
        ChatAsync(request, astronautId, options, contexts, provider, cancellationToken);

    private static async Task<IResult> ChatAsync(
        AssistantRequest request,
        Guid astronautId,
        IOptions<AiOptions> options,
        AssistantContextBuilder contexts,
        IAssistantProvider provider,
        CancellationToken cancellationToken)
    {
        string question = request.Question?.Trim() ?? string.Empty;
        if (question.Length is 0 or > MaxQuestionLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["question"] = [$"Question is required and must be at most {MaxQuestionLength} characters."],
            });
        }

        AiOptions settings = options.Value;
        if (!settings.Enabled)
        {
            return Results.Problem(title: "AI assistant is disabled", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!settings.IsConfigured)
        {
            return Results.Problem(title: "AI assistant is not configured", detail: "Configure AI__ApiKey and AI__Model on the server.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        AssistantContext? context = await contexts.BuildAsync(astronautId, cancellationToken);
        if (context is null)
        {
            return Results.Problem(title: "Astronaut profile not found", statusCode: StatusCodes.Status404NotFound);
        }

        string language = string.Equals(request.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "es";
        try
        {
            AssistantProviderResult generated = await provider.AskAsync(context, question, language, cancellationToken);
            return Results.Ok(new AssistantResponse(
                generated.Answer,
                context.GeneratedAt,
                generated.Provider,
                generated.Model,
                Suggestions(context.Risk.Status, context.OpenAlerts.Count, language),
                language == "en"
                    ? "Decision-support information only; not a medical diagnosis. Follow official emergency procedures."
                    : "Información de apoyo a decisiones; no es un diagnóstico médico. Sigue los procedimientos oficiales de emergencia."));
        }
        catch (AssistantProviderException)
        {
            return Results.Problem(title: "AI assistant is temporarily unavailable", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IReadOnlyList<string> Suggestions(HealthStatus status, int alerts, string language)
    {
        if (language == "en")
        {
            return status == HealthStatus.Critical || alerts > 0
                ? ["What needs attention right now?", "Which data triggered the alert?", "What official action is displayed?"]
                : ["What is my current status?", "Is there anything to watch?", "Which data is simulated?"];
        }

        return status == HealthStatus.Critical || alerts > 0
            ? ["¿Qué necesita atención ahora?", "¿Qué dato activó la alerta?", "¿Qué acción oficial se muestra?"]
            : ["¿Cuál es mi estado actual?", "¿Hay algo que vigilar?", "¿Qué datos son simulados?"];
    }
}
