namespace Heartbeat.Client;

public sealed class ConnectivityChangedEventArgs(bool isOnline) : EventArgs
{
    public bool IsOnline { get; } = isOnline;
}