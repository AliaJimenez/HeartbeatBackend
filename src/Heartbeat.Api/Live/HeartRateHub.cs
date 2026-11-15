using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Heartbeat.Api.Live;

/// <summary>
/// Hub de solo difusión: el servidor envía <c>HeartRate</c> y su evaluación <c>HeartRateStatus</c> con cada lectura,
/// solo al grupo del astronauta dueño del reloj. Exige token (en WebSockets, <c>?access_token=</c>). Un tripulante
/// recibe el suyo; MC con <c>vitals.view</c> elige uno con <c>?astronautId=</c>.
/// </summary>
[Authorize(Policy = AuthConstants.AstronautDataPolicy)]
public sealed class HeartRateHub(AppDbContext db) : Hub
{
    public static string GroupFor(Guid astronautId) => $"astronaut:{astronautId:N}";

    public override async Task OnConnectedAsync()
    {
        Guid? requested = Guid.TryParse(Context.GetHttpContext()?.Request.Query["astronautId"], out var id) ? id : null;
        var access = await AstronautAccess.ResolveAsync(
            Context.User!, requested, db, mcMustSpecify: true, Context.ConnectionAborted);

        if (!access.Allowed)
        {
            throw new HubException(access.Message);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(access.AstronautId!.Value));
        await base.OnConnectedAsync();
    }
}
