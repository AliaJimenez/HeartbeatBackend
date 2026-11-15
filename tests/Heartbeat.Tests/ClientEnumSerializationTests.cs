using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Heartbeat.Client;
using Heartbeat.Shared;

namespace Heartbeat.Tests;

/// <summary>
/// La API habla con enums en texto camelCase (Program.cs: <c>JsonStringEnumConverter(CamelCase)</c>) y así lo documenta
/// contracts/CONTRACTS.md. El cliente tiene que leer y escribir ese mismo formato.
/// </summary>
public class ClientEnumSerializationTests
{
    // Las mismas opciones que usa la API para responder.
    private static readonly JsonSerializerOptions ServerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json"),
            };
        }
    }

    private static CrewApiClient ClientFor(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

    private static string ServerJson<T>(T value) => JsonSerializer.Serialize(value, ServerOptions);

    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 35, 0, TimeSpan.Zero);

    [Fact]
    public async Task Sync_ReadsInboundMessagesWhoseEnumsComeAsText()
    {
        var inbound = new MessageDto(
            Guid.NewGuid(), MessageDirection.ToAstronaut, MessageKind.Report, Guid.NewGuid(),
            "Descansa", "Descansa 24 h", Now, Now, null, "Dr. Okafor");
        var response = new SyncResponse(Now, [], [], null, new SyncInbound([inbound], []), false);
        var handler = new StubHandler(_ => ServerJson(response));
        using var client = ClientFor(handler);
        client.SetTokens("a", "r", "dev");

        var result = await client.SyncAsync(new SyncRequest("dev", null, "v", null, [], []));

        var message = Assert.Single(result.Inbound.Messages);
        Assert.Equal(MessageDirection.ToAstronaut, message.Direction);
        Assert.Equal(MessageKind.Report, message.Kind);
        Assert.Equal(inbound.Id, message.Id);
    }

    [Fact]
    public async Task Sync_ReadsTheServersRealWireFormat()
    {
        // Tal cual lo devuelve la API (camelCase, enums en texto).
        const string json = """
            { "serverTime": "2026-11-14T09:35:00Z", "accepted": [], "rejected": [], "profile": null,
              "inbound": { "messages": [ { "id": "0193e000-0000-7000-8000-000000000001", "direction": "toAstronaut", "kind": "message",
                "checkUpId": null, "subject": "Hola", "body": "Texto", "createdAt": "2026-11-14T09:30:00Z",
                "receivedAt": null, "readAt": null, "senderName": "Mission Control" } ],
                "acknowledgedAlerts": [] },
              "rangesUpdateAvailable": false }
            """;
        using var client = ClientFor(new StubHandler(_ => json));
        client.SetTokens("a", "r", "dev");

        var result = await client.SyncAsync(new SyncRequest("dev", null, "v", null, [], []));

        Assert.Equal(MessageDirection.ToAstronaut, Assert.Single(result.Inbound.Messages).Direction);
    }

    [Fact]
    public async Task GetCheckUps_ReadsStatusIndicatorAndSourceAsText()
    {
        var checkUp = new CheckUpDto(
            Guid.NewGuid(), Now, Now, HealthStatus.Critical, "2026.11.14-1", "nota", null,
            [new ReadingDto(Guid.NewGuid(), IndicatorType.BloodPressure, 185, 95, HealthStatus.Critical, ["bp-sys-critical-high"], ReadingSource.Wearable, "reloj")]);
        var page = new Paged<CheckUpDto>([checkUp], 1, 50, 1);
        using var client = ClientFor(new StubHandler(_ => ServerJson(page)));
        client.SetTokens("a", "r", "dev");

        var result = await client.GetCheckUpsAsync();

        var read = Assert.Single(result.Items);
        Assert.Equal(HealthStatus.Critical, read.OverallStatus);
        var reading = Assert.Single(read.Readings);
        Assert.Equal(IndicatorType.BloodPressure, reading.Indicator);
        Assert.Equal(ReadingSource.Wearable, reading.Source);
    }

    [Fact]
    public async Task Sync_SendsEnumsAsTheTextTheContractDocuments()
    {
        var handler = new StubHandler(_ => ServerJson(new SyncResponse(Now, [], [], null, new SyncInbound([], []), false)));
        using var client = ClientFor(handler);
        client.SetTokens("a", "r", "dev");
        var checkUp = new CheckUpDto(
            Guid.NewGuid(), Now, null, HealthStatus.Critical, "v", null, null,
            [new ReadingDto(Guid.NewGuid(), IndicatorType.HeartRate, 130, null, HealthStatus.Critical, ["hr-critical-high"])]);
        var message = new MessageDto(
            Guid.NewGuid(), MessageDirection.ToMissionControl, MessageKind.Report, checkUp.Id, "s", "b", Now, null, null, null);

        await client.SyncAsync(new SyncRequest("dev", null, "v", null, [checkUp], [message]));

        var body = Assert.Single(handler.Bodies);
        Assert.Contains("\"overallStatus\":\"critical\"", body);
        Assert.Contains("\"indicator\":\"heartRate\"", body);
        Assert.Contains("\"source\":\"manual\"", body);
        Assert.Contains("\"direction\":\"toMissionControl\"", body);
        Assert.Contains("\"kind\":\"report\"", body);
    }

    [Fact]
    public async Task WhatTheClientSends_IsReadBackByTheServersOptions()
    {
        var handler = new StubHandler(_ => ServerJson(new SyncResponse(Now, [], [], null, new SyncInbound([], []), false)));
        using var client = ClientFor(handler);
        client.SetTokens("a", "r", "dev");
        var message = new MessageDto(
            Guid.NewGuid(), MessageDirection.ToMissionControl, MessageKind.Message, null, "s", "b", Now, null, null, null);

        await client.SyncAsync(new SyncRequest("dev", null, "v", null, [], [message]));

        var received = JsonSerializer.Deserialize<SyncRequest>(Assert.Single(handler.Bodies), ServerOptions)!;
        Assert.Equal(MessageDirection.ToMissionControl, Assert.Single(received.Messages).Direction);
    }
}
