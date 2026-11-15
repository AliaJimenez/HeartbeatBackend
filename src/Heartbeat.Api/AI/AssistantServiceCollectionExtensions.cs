using Microsoft.Extensions.Options;

namespace Heartbeat.Api.AI;

public static class AssistantServiceCollectionExtensions
{
    public static IServiceCollection AddHeartbeatAssistant(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AssistantContextBuilder>();
        services.AddHttpClient<IAssistantProvider, AnthropicAssistantProvider>((provider, client) =>
        {
            AiOptions options = provider.GetRequiredService<IOptions<AiOptions>>().Value;
            client.BaseAddress = Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out Uri? uri)
                ? uri
                : new Uri("https://api.anthropic.com/");
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 60));
        });
        return services;
    }
}
