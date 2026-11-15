using Microsoft.AspNetCore.SignalR;

namespace Heartbeat.Api.Live;

/// <summary>Hub de solo difusión: el servidor envía el evento <c>HeartRate</c> con cada lectura del reloj.</summary>
public sealed class HeartRateHub : Hub
{
}
