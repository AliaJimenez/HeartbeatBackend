using System.Security.Cryptography;
using System.Text;

namespace Heartbeat.Api.Live;

/// <summary>Claves de reloj: 256 bits aleatorios; en la base solo se guarda el hash SHA-256.</summary>
public static class WatchKeys
{
    public const string HeaderName = "X-Watch-Key";

    public static string NewKey()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
