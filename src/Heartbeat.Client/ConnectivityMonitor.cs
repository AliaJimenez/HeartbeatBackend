using System.Net.NetworkInformation;

namespace Heartbeat.Client;

internal sealed class ConnectivityMonitor : IDisposable
{
    public bool IsOnline => NetworkInterface.GetIsNetworkAvailable();

    public event EventHandler<ConnectivityChangedEventArgs>? ConnectivityChanged;

    public ConnectivityMonitor()
    {
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        ConnectivityChanged?.Invoke(this, new ConnectivityChangedEventArgs(e.IsAvailable));
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
    }
}