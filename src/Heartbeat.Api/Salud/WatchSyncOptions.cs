namespace Heartbeat.Api.Salud;

/// <summary>
/// Conexión con el backend Python "WatchSync API" (sección <c>WatchSync</c> de la configuración).
/// La contraseña NO va en appsettings: <c>dotnet user-secrets set "WatchSync:Password" "..."</c>.
/// </summary>
public sealed class WatchSyncOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8000/api/v1";

    public string Email { get; set; } = "";

    public string Password { get; set; } = "";
}
