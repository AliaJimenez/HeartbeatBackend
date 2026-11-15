using System.Globalization;
using Heartbeat.Api.Auth;

namespace Heartbeat.Api.Salud;

/// <summary>
/// Datos de Samsung Health (resumen, FC histórica, sueño, actividad, presión y sincronización) leídos del backend
/// Python y reexpuestos tal cual. Exigen sesión, como el resto de rutas de datos (token de tripulante o de MC).
/// </summary>
public static class SaludEndpoints
{
    private static readonly string[] Resolutions = ["raw", "5m", "1h", "1d"];

    public static IEndpointRouteBuilder MapSalud(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/salud")
            .RequireAuthorization(policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim("aud", AuthConstants.CrewAudience, AuthConstants.McAudience));

        group.MapGet("/resumen", GetResumenAsync);
        group.MapGet("/fc", GetFcAsync);
        group.MapGet("/sueno", GetSuenoAsync);
        group.MapGet("/actividad", GetActividadAsync);
        group.MapGet("/presion", GetPresionAsync);
        group.MapGet("/estado", GetEstadoAsync);

        return app;
    }

    private static Task<IResult> GetResumenAsync(WatchSyncClient client, string? fecha, CancellationToken ct)
    {
        DateOnly date = DateOnly.FromDateTime(DateTime.Now);
        if (!string.IsNullOrWhiteSpace(fecha)
            && !DateOnly.TryParseExact(fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return Task.FromResult(Error(StatusCodes.Status400BadRequest, "fecha debe tener el formato yyyy-MM-dd"));
        }

        return ProxyAsync(() => client.GetSummaryAsync(date, ct));
    }

    private static Task<IResult> GetFcAsync(
        WatchSyncClient client, DateTimeOffset? desde, DateTimeOffset? hasta, string? resolucion, CancellationToken ct)
    {
        string resolution = string.IsNullOrWhiteSpace(resolucion) ? "1h" : resolucion;
        if (!Resolutions.Contains(resolution))
        {
            return Task.FromResult(Error(StatusCodes.Status400BadRequest, "resolucion debe ser raw, 5m, 1h o 1d"));
        }

        DateTimeOffset to = hasta ?? DateTimeOffset.Now;
        DateTimeOffset from = desde ?? (resolution == "raw" ? to.AddHours(-24) : to.AddDays(-7));
        return ProxyAsync(() => client.GetHeartRateAsync(from, to, resolution, ct));
    }

    private static Task<IResult> GetSuenoAsync(WatchSyncClient client, DateTimeOffset? desde, DateTimeOffset? hasta, CancellationToken ct)
    {
        (DateTimeOffset from, DateTimeOffset to) = LastWeek(desde, hasta);
        return ProxyAsync(() => client.GetSleepAsync(from, to, ct));
    }

    private static Task<IResult> GetActividadAsync(WatchSyncClient client, DateOnly? desde, DateOnly? hasta, CancellationToken ct)
    {
        DateOnly to = hasta ?? DateOnly.FromDateTime(DateTime.Now);
        DateOnly from = desde ?? to.AddDays(-6);
        return ProxyAsync(() => client.GetDailyActivityAsync(from, to, ct));
    }

    private static Task<IResult> GetPresionAsync(WatchSyncClient client, DateTimeOffset? desde, DateTimeOffset? hasta, CancellationToken ct)
    {
        (DateTimeOffset from, DateTimeOffset to) = LastWeek(desde, hasta);
        return ProxyAsync(() => client.GetBloodPressureAsync(from, to, ct));
    }

    private static Task<IResult> GetEstadoAsync(WatchSyncClient client, CancellationToken ct) =>
        ProxyAsync(() => client.GetSyncStatusAsync(ct));

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
