namespace Heartbeat.Api.Salud;

/// <summary>
/// Conexión con el backend Python "WatchSync API" (sección <c>WatchSync</c> de la configuración).
/// La contraseña NO va en appsettings: <c>dotnet user-secrets set "WatchSync:Password" "..."</c>.
/// </summary>
public sealed class WatchSyncOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8000/api/v1";

    /// <summary>Cuenta por defecto: la que usa quien no tiene una cuenta propia en <see cref="Accounts"/>.</summary>
    public string Email { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>Frecuencia del importador de sueño y presión arterial. Un fallo se reintenta en la vuelta siguiente.</summary>
    public int ImportIntervalMinutes { get; set; } = 5;

    /// <summary>Ventana que se vuelve a consultar; los ids externos hacen que repetirla sea idempotente.</summary>
    public int ImportLookbackDays { get; set; } = 3;

    /// <summary>
    /// Cuentas de WatchSync por astronauta (opcional), con el <c>UserName</c> del astronauta como clave:
    /// <c>WatchSync:Accounts:a.rivera:Email</c> y <c>...:Password</c> (user-secrets o variables de entorno).
    /// </summary>
    public Dictionary<string, WatchSyncAccount> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WatchSyncAccount
{
    public string Email { get; set; } = "";

    public string Password { get; set; } = "";
}
