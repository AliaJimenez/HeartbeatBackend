namespace Heartbeat.Api.AI;

public interface IAssistantProvider
{
    Task<AssistantProviderResult> AskAsync(
        AssistantContext context,
        string question,
        string language,
        CancellationToken cancellationToken);
}

public sealed class AssistantProviderException(string message, Exception? innerException = null)
    : Exception(message, innerException);
