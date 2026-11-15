using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Heartbeat.Api.Live;

public static class LiveEndpoints
{
    public const int MinBpm = 25;
    public const int MaxBpm = 250;

    /// <summary>
    /// El reloj envía con su clave (<c>X-Watch-Key</c>); las lecturas se guardan en el búfer del astronauta dueño del reloj.
    /// Consultarlas exige token (política <see cref="AuthConstants.AstronautDataPolicy"/>).
    /// </summary>
    public static IEndpointRouteBuilder MapLiveHeartRate(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/heartrate", PostAsync).AllowAnonymous().DisableAntiforgery();

        var read = app.MapGroup("/api/heartrate").RequireAuthorization(AuthConstants.AstronautDataPolicy);
        read.MapGet("/latest", GetLatestAsync);
        read.MapGet("/recent", GetRecentAsync);
        return app;
    }

    private static async Task<IResult> PostAsync(
        HttpRequest request,
        HeartRateReading r,
        AppDbContext db,
        HeartRateStore store,
        IHubContext<HeartRateHub> hubContext,
        CancellationToken ct)
    {
        var key = request.Headers[WatchKeys.HeaderName].ToString().Trim();
        if (key.Length == 0)
        {
            return Results.Problem(title: "Missing watch key", statusCode: StatusCodes.Status401Unauthorized);
        }

        var hash = WatchKeys.Hash(key);
        var astronautId = await db.WatchDevices.AsNoTracking()
            .Where(w => w.KeyHash == hash)
            .Select(w => (Guid?)w.AstronautId)
            .FirstOrDefaultAsync(ct);
        if (astronautId is null)
        {
            return Results.Problem(title: "Unknown watch key", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (r is null || r.Bpm < MinBpm || r.Bpm > MaxBpm)
        {
            return Results.BadRequest(new { error = "bpm fuera de rango" });
        }

        store.For(astronautId.Value).Add(r);
        await hubContext.Clients.Group(HeartRateHub.GroupFor(astronautId.Value)).SendAsync("HeartRate", r, ct);
        return Results.Ok();
    }

    private static async Task<IResult> GetLatestAsync(
        ClaimsPrincipal user, AppDbContext db, HeartRateStore store, Guid? astronautId, CancellationToken ct)
    {
        var access = await AstronautAccess.ResolveAsync(user, astronautId, db, mcMustSpecify: true, ct);
        if (!access.Allowed)
        {
            return access.ToProblem();
        }

        return store.Find(access.AstronautId!.Value)?.Latest is { } latest ? Results.Ok(latest) : Results.NoContent();
    }

    private static async Task<IResult> GetRecentAsync(
        ClaimsPrincipal user, AppDbContext db, HeartRateStore store, Guid? astronautId, int? seconds, CancellationToken ct)
    {
        var access = await AstronautAccess.ResolveAsync(user, astronautId, db, mcMustSpecify: true, ct);
        if (!access.Allowed)
        {
            return access.ToProblem();
        }

        int window = Math.Clamp(seconds ?? 300, 1, HeartRateBuffer.Capacity);
        var readings = store.Find(access.AstronautId!.Value)?.Since(DateTimeOffset.UtcNow.AddSeconds(-window)) ?? [];
        return Results.Ok(readings);
    }
}
