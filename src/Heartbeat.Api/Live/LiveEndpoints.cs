using Microsoft.AspNetCore.SignalR;

namespace Heartbeat.Api.Live;

public static class LiveEndpoints
{
    public const int MinBpm = 25;
    public const int MaxBpm = 250;

    /// <summary>Rutas anónimas del reloj: recibir lecturas y consultar las últimas.</summary>
    public static IEndpointRouteBuilder MapLiveHeartRate(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/heartrate", PostAsync).AllowAnonymous().DisableAntiforgery();
        app.MapGet("/api/heartrate/latest", GetLatest).AllowAnonymous();
        app.MapGet("/api/heartrate/recent", GetRecent).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> PostAsync(
        HeartRateReading r, HeartRateBuffer buffer, IHubContext<HeartRateHub> hubContext, CancellationToken ct)
    {
        if (r is null || r.Bpm < MinBpm || r.Bpm > MaxBpm)
        {
            return Results.BadRequest(new { error = "bpm fuera de rango" });
        }

        buffer.Add(r);
        await hubContext.Clients.All.SendAsync("HeartRate", r, ct);
        return Results.Ok();
    }

    private static IResult GetLatest(HeartRateBuffer buffer) =>
        buffer.Latest is { } latest ? Results.Ok(latest) : Results.NoContent();

    private static IResult GetRecent(HeartRateBuffer buffer, int? seconds)
    {
        int window = Math.Clamp(seconds ?? 300, 1, HeartRateBuffer.Capacity);
        return Results.Ok(buffer.Since(DateTimeOffset.UtcNow.AddSeconds(-window)));
    }
}
