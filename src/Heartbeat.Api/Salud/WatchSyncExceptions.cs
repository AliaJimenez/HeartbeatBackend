namespace Heartbeat.Api.Salud;

/// <summary>El backend Python no respondió (caído o fuera de tiempo).</summary>
public sealed class WatchSyncUnavailableException : Exception
{
    public WatchSyncUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>El backend Python respondió con un código que no es 2xx. <see cref="Exception.Message"/> es su campo <c>message</c>.</summary>
public sealed class WatchSyncApiException : Exception
{
    public WatchSyncApiException(int statusCode, string? code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }

    public string? Code { get; }
}
