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
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            // Some networks (hotspots, proxies) reset the TLS 1.3 handshake of .NET: the assistant answered 502 although
            // api.anthropic.com was reachable. TLS 1.2 goes through everywhere and is enough for this API.
            SslOptions = { EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 },
        });
        return services;
    }
}
