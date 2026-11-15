using System.Globalization;
using System.Security.Claims;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Live;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Salud;

/// <summary>
/// Datos de Samsung Health (resumen, FC histórica, sueño, actividad, presión y sincronización) leídos del backend
/// Python y reexpuestos tal cual. Exigen sesión: un tripulante usa la cuenta de WatchSync asociada a él (o la de por defecto);
/// MC con <c>vitals.view</c> puede pedir la de un astronauta con <c>?astronautId=</c> (sin él, la de por defecto).
/// </summary>
public static class SaludEndpoints
{
    private static readonly string[] Resolutions = ["raw", "5m", "1h", "1d"];

    public static IEndpointRouteBuilder MapSalud(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/salud")
            .RequireAuthorization(AuthConstants.AstronautDataPolicy);

        group.MapGet("/resumen", GetResumenAsync);
        group.MapGet("/fc", GetFcAsync);
        group.MapGet("/sueno", GetSuenoAsync);
        group.MapGet("/actividad", GetActividadAsync);
        group.MapGet("/presion", GetPresionAsync);
        group.MapGet("/estado", GetEstadoAsync);

        return app;
    }

    private static Task<IResult> GetResumenAsync(
        ClaimsPrincipal user, AppDbContext db, IWatchSyncClients clients, Guid? astronautId, string? fecha, CancellationToken ct)
    {
        DateOnly date = DateOnly.FromDateTime(DateTime.Now);
        if (!string.IsNullOrWhiteSpace(fecha)
            && !DateOnly.TryParseExact(fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return Task.FromResult(Error(StatusCodes.Status400BadRequest, "fecha debe tener el formato yyyy-MM-dd"));
        }

        return WithClientAsync(user, db, clients, astronautId, ct, client => ProxyAsync(() => client.GetSummaryAsync(date, ct)));
    }

    private static Task<IResult> GetFcAsync(
        ClaimsPrincipal user, AppDbContext db, IWatchSyncClients clients, Guid? astronautId,
        DateTimeOffset? desde, DateTimeOffset? hasta, string? resolucion, CancellationToken ct)
    {
        string resolution = string.IsNullOrWhiteSpace(resolucion) ? "1h" : resolucion;
        if (!Resolutions.Contains(resolution))
        {
            return Task.FromResult(Error(StatusCodes.Status400BadRequest, "resolucion debe ser raw, 5m, 1h o 1d"));
        }

        DateTimeOffset to = hasta ?? DateTimeOffset.Now;
        DateTimeOffset from = desde ?? (resolution == "raw" ? to.AddHours(-24) : to.AddDays(-7));
        return WithClientAsync(user, db, clients, astronautId, ct, client => ProxyAsync(() => client.GetHeartRateAsync(from, to, resolution, ct)));
    }

    private static Task<IResult> GetSuenoAsync(
        ClaimsPrincipal user, AppDbContext db, IWatchSyncClients clients, Guid? astronautId,
        DateTimeOffset? desde, DateTimeOffset? hasta, CancellationToken ct)
    {
        (DateTimeOffset from, DateTimeOffset to) = LastWeek(desde, hasta);
        return WithClientAsync(user, db, clients, astronautId, ct, client => ProxyAsync(() => client.GetSleepAsync(from, to, ct)));
    }

    private static Task<IResult> GetActividadAsync(
        ClaimsPrincipal user, AppDbContext db, IWatchSyncClients clients, Guid? astronautId,
        DateOnly? desde, DateOnly? hasta, CancellationToken ct)
    {
        DateOnly to = hasta ?? DateOnly.FromDateTime(DateTime.Now);
        DateOnly from = desde ?? to.AddDays(-6);
        return WithClientAsync(user, db, clients, astronautId, ct, client => ProxyAsync(() => client.GetDailyActivityAsync(from, to, ct)));
    }

    private static Task<IResult> GetPresionAsync(
        ClaimsPrincipal user, AppDbContext db, IWatchSyncClients clients, Guid? astronautId,
        DateTimeOffset? desde, DateTimeOffset? hasta, CancellationToken ct)
    {
        (DateTimeOffset from, DateTimeOffset to) = LastWeek(desde, hasta);
        return WithClientAsync(user, db, clients, astronautId, ct, client => ProxyAsync(() => client.GetBloodPressureAsync(from, to, ct)));
    }

    private static Task<IResult> GetEstadoAsync(
        ClaimsPrincipal user, AppDbContext db, IWatchSyncClients clients, Guid? astronautId, CancellationToken ct) =>
        WithClientAsync(user, db, clients, astronautId, ct, client => ProxyAsync(() => client.GetSyncStatusAsync(ct)));

    /// <summary>
    /// Aplica la regla de acceso y elige la cuenta de WatchSync: la asociada al astronauta (por su <c>UserName</c>) o la de
    /// por defecto. Un tripulante solo puede pedir lo suyo; MC sin <c>astronautId</c> usa la cuenta por defecto.
    /// </summary>
    private static async Task<IResult> WithClientAsync(
        ClaimsPrincipal user, AppDbContext db, IWatchSyncClients clients, Guid? astronautId, CancellationToken ct,
        Func<WatchSyncClient, Task<IResult>> call)
    {
        var access = await AstronautAccess.ResolveAsync(user, astronautId, db, mcMustSpecify: false, ct);
        if (!access.Allowed)
        {
            return access.ToProblem();
        }

        string? userName = access.AstronautId is { } id
            ? await db.Astronauts.AsNoTracking().Where(a => a.Id == id).Select(a => a.UserName).FirstOrDefaultAsync(ct)
            : null;

        return await call(clients.For(userName));
    }

    private static (DateTimeOffset From, DateTimeOffset To) LastWeek(DateTimeOffset? desde, DateTimeOffset? hasta)
    {
        DateTimeOffset to = hasta ?? DateTimeOffset.Now;
        return (desde ?? to.AddDays(-7), to);
    }

    private static async Task<IResult> ProxyAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return Results.Ok(await call());
        }
        catch (WatchSyncUnavailableException)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "El servidor de datos de Samsung Health no responde");
        }
        catch (WatchSyncApiException ex)
        {
            int status = ex.StatusCode switch
            {
                StatusCodes.Status400BadRequest or StatusCodes.Status422UnprocessableEntity or StatusCodes.Status404NotFound => ex.StatusCode,
                _ => StatusCodes.Status502BadGateway,
            };
            return Error(status, ex.Message);
        }
    }

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
