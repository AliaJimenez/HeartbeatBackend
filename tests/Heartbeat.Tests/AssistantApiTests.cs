using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Heartbeat.Api.AI;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Heartbeat.Tests;

public sealed class AssistantApiTests : IDisposable
{
    private sealed class FakeProvider : IAssistantProvider
    {
        public AssistantContext? Context { get; private set; }
        public string? Question { get; private set; }

        public Task<AssistantProviderResult> AskAsync(
            AssistantContext context, string question, string language, CancellationToken cancellationToken)
        {
            Context = context;
            Question = question;
            return Task.FromResult(new AssistantProviderResult("Situación explicada de forma simple.", "Fake", "test-model"));
        }
    }

    private sealed class Factory(FakeProvider provider) : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-ai-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_dbPath}");
            builder.UseSetting("AI:Enabled", "true");
            builder.UseSetting("AI:Provider", "Anthropic");
            builder.UseSetting("AI:ApiKey", "test-key");
            builder.UseSetting("AI:Model", "test-model");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAssistantProvider>();
                services.AddSingleton<IAssistantProvider>(provider);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(_dbPath); } catch (IOException) { }
        }
    }

    private readonly FakeProvider _provider = new();
    private readonly Factory _factory;
    private readonly Guid _astronautId = Guid.NewGuid();

    public AssistantApiTests()
    {
        _factory = new Factory(_provider);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mission = new Mission { Id = Guid.NewGuid(), Name = "Artemis AI", StartsAt = DateTimeOffset.UtcNow.AddDays(-2) };
        db.Missions.Add(mission);
        db.Astronauts.Add(new Astronaut
        {
            Id = _astronautId,
            UserName = "ana",
            DisplayName = "Ana",
            MissionId = mission.Id,
            Notes = "This private note must not enter the assistant context.",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Chat_requires_a_crew_session_and_uses_only_the_callers_context()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().PostAsJsonAsync("/api/crew/v1/assistant/chat", new AssistantRequest("Estado"))).StatusCode);

        var client = _factory.CreateClient();
        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(_astronautId, "Ana", AuthConstants.CrewAudience));

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/crew/v1/assistant/chat", new AssistantRequest("  ¿Qué necesita atención?  ", "es"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssistantResponse result = (await response.Content.ReadFromJsonAsync<AssistantResponse>())!;
        Assert.Equal("Situación explicada de forma simple.", result.Answer);
        Assert.Equal("Fake", result.Provider);
        Assert.Equal(_astronautId, _provider.Context!.AstronautId);
        Assert.Equal("Artemis AI", _provider.Context.MissionName);
        Assert.Equal("¿Qué necesita atención?", _provider.Question);
    }

    [Fact]
    public async Task Empty_or_oversized_questions_are_rejected_before_calling_the_provider()
    {
        var client = _factory.CreateClient();
        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(_astronautId, "Ana", AuthConstants.CrewAudience));

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/crew/v1/assistant/chat", new AssistantRequest(" "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/crew/v1/assistant/chat", new AssistantRequest(new string('x', 1_001)))).StatusCode);
    }

    [Fact]
    public async Task Anthropic_provider_sends_the_required_headers_and_reads_text_blocks()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/") };
        var options = Options.Create(new AiOptions
        {
            Enabled = true,
            ApiKey = "secret-test-key",
            Model = "claude-test",
        });
        var provider = new AnthropicAssistantProvider(http, options, NullLogger<AnthropicAssistantProvider>.Instance);
        var context = _provider.Context ?? new AssistantContext(
            _astronautId, "Ana", "Artemis", DateTimeOffset.UtcNow,
            Heartbeat.Shared.RiskEngine.Evaluate(new Heartbeat.Shared.RiskInputs()), []);

        AssistantProviderResult result = await provider.AskAsync(context, "¿Qué ocurre?", "es", default);

        Assert.Equal("Respuesta de Claude", result.Answer);
        Assert.Equal("secret-test-key", handler.ApiKey);
        Assert.Equal("2023-06-01", handler.Version);
        Assert.Contains("claude-test", handler.Body);
        using JsonDocument sent = JsonDocument.Parse(handler.Body);
        Assert.Contains("¿Qué ocurre?", sent.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? ApiKey { get; private set; }
        public string? Version { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ApiKey = request.Headers.GetValues("x-api-key").Single();
            Version = request.Headers.GetValues("anthropic-version").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"content":[{"type":"text","text":"Respuesta de Claude"}]}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
