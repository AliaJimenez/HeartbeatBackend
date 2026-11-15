using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Live;

/// <summary>Registro de relojes de Mission Control (WR-BIND): DeviceId, clave por dispositivo y astronauta dueño. Todo con <c>crew.manage</c>.</summary>
public static class McWatchesEndpoints
{
    public static IEndpointRouteBuilder MapMcWatchesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mc/v1/watches");

        group.MapGet("/", ListAsync).RequirePermission(Permissions.CrewManage);
        group.MapPost("/", RegisterAsync).RequirePermission(Permissions.CrewManage);
        group.MapPost("/{id:guid}/rotate-key", RotateKeyAsync).RequirePermission(Permissions.CrewManage);
        group.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(Permissions.CrewManage);

        return app;
    }

    private static async Task<IResult> ListAsync(AppDbContext db, Guid? astronautId, CancellationToken ct)
    {
        var query = db.WatchDevices.AsNoTracking().AsQueryable();
        if (astronautId is not null)
        {
            query = query.Where(w => w.AstronautId == astronautId);
        }

        var watches = await query.OrderBy(w => w.DeviceId).ToListAsync(ct);
        return Results.Ok(watches.Select(ToDto).ToList());
    }

    private static async Task<IResult> RegisterAsync(RegisterWatchRequest request, AppDbContext db, CancellationToken ct)
    {
        var deviceId = request.DeviceId?.Trim() ?? string.Empty;
        var label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();

        var errors = new Dictionary<string, string[]>();
        if (deviceId.Length is 0 or > WatchDevice.DeviceIdMaxLength)
        {
            errors["deviceId"] = [$"DeviceId is required and must be at most {WatchDevice.DeviceIdMaxLength} characters."];
        }

        if (label is { Length: > WatchDevice.LabelMaxLength })
        {
            errors["label"] = [$"Label must be at most {WatchDevice.LabelMaxLength} characters."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (!await db.Astronauts.AnyAsync(a => a.Id == request.AstronautId, ct))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["astronautId"] = ["Astronaut does not exist."] });
        }

        if (await db.WatchDevices.AnyAsync(w => w.DeviceId == deviceId, ct))
        {
            return Conflict();
        }

        var key = WatchKeys.NewKey();
        var watch = new WatchDevice
        {
            Id = Guid.CreateVersion7(),
            DeviceId = deviceId,
            KeyHash = WatchKeys.Hash(key),
            AstronautId = request.AstronautId,
            Label = label,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WatchDevices.Add(watch);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Conflict();
        }

        return Results.Created($"/api/mc/v1/watches/{watch.Id}", new WatchRegisteredDto(ToDto(watch), key));
    }

    private static async Task<IResult> RotateKeyAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var watch = await db.WatchDevices.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (watch is null)
        {
            return NotFound();
        }

        var key = WatchKeys.NewKey();
        watch.KeyHash = WatchKeys.Hash(key);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new WatchRegisteredDto(ToDto(watch), key));
    }

    private static async Task<IResult> DeleteAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var watch = await db.WatchDevices.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (watch is null)
        {
            return NotFound();
        }

        db.WatchDevices.Remove(watch);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static WatchDeviceDto ToDto(WatchDevice w) => new(w.Id, w.DeviceId, w.AstronautId, w.Label, w.CreatedAt);

    private static IResult NotFound() => Results.Problem(title: "Watch not found", statusCode: StatusCodes.Status404NotFound);

    private static IResult Conflict() => Results.Problem(title: "DeviceId already registered", statusCode: StatusCodes.Status409Conflict);
}
