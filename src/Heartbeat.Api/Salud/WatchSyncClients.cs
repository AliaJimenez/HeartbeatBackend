using System.Collections.Concurrent;
using System.Net;

namespace Heartbeat.Api.Salud;

/// <summary>Elige el cliente de WatchSync (y por tanto la cuenta) de cada astronauta.</summary>
public interface IWatchSyncClients
{
    /// <summary>El cliente de la cuenta asociada al astronauta, o el de la cuenta por defecto si no tiene una propia.</summary>
    WatchSyncClient For(string? astronautUserName);
}

/// <summary>
/// Un <see cref="WatchSyncClient"/> por cuenta, cada uno con su propia sesión (cookie). Las cuentas salen de
/// <see cref="WatchSyncOptions"/>: <see cref="WatchSyncOptions.Accounts"/> por astronauta y, si no hay, la cuenta por defecto.
/// </summary>
public sealed class WatchSyncClients : IWatchSyncClients, IDisposable
{
    private readonly WatchSyncOptions _options;
    private readonly ConcurrentDictionary<string, Lazy<WatchSyncClient>> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<HttpClient> _http = [];

    public WatchSyncClients(WatchSyncOptions options) => _options = options;

    public WatchSyncClient For(string? astronautUserName)
    {
        var (email, password) = astronautUserName is not null
            && FindAccount(astronautUserName) is { } account
            && !string.IsNullOrWhiteSpace(account.Email)
                ? (account.Email, account.Password)
                : (_options.Email, _options.Password);

        return _clients.GetOrAdd(email, _ => new Lazy<WatchSyncClient>(() => Create(email, password))).Value;
    }

    private WatchSyncAccount? FindAccount(string astronautUserName)
        => _options.Accounts.FirstOrDefault(a => string.Equals(a.Key, astronautUserName, StringComparison.OrdinalIgnoreCase)).Value;

    private WatchSyncClient Create(string email, string password)
    {
        var http = new HttpClient(new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.Add(http);
        return new WatchSyncClient(http, email, password);
    }

    public void Dispose()
    {
        foreach (var http in _http)
        {
            http.Dispose();
        }
    }
}
