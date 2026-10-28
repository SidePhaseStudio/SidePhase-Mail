using Windows.Networking.Connectivity;

namespace SidePhase.Mail.App.Services;

/// <summary>
/// Whether this PC has an internet connection, from Windows' own network status. Used to show the offline bar, to
/// avoid server actions that can only fail (marking as read while offline), and to resume syncing and sending the
/// moment the connection comes back.
/// </summary>
public sealed class ConnectivityService : IDisposable
{
    private bool _isOnline;

    public ConnectivityService()
    {
        _isOnline = Detect();
        try
        {
            NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        }
        catch (Exception ex)
        {
            // Without notifications the app still works: it simply finds out about connections on its next sync.
            StartupDiagnostics.Log("network status unavailable", ex);
        }
    }

    public bool IsOnline => _isOnline;

    /// <summary>Raised (on a background thread) when the connection is lost or comes back; the argument is the new state.</summary>
    public event EventHandler<bool>? Changed;

    private void OnNetworkStatusChanged(object sender)
    {
        var online = Detect();
        if (online == _isOnline) return;
        _isOnline = online;
        Changed?.Invoke(this, online);
    }

    private static bool Detect()
    {
        try
        {
            var level = NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel();
            // Constrained internet (captive portal) still counts as offline for mail.
            return level == NetworkConnectivityLevel.InternetAccess;
        }
        catch (Exception)
        {
            return true; // if Windows can't say, don't claim to be offline
        }
    }

    public void Dispose()
    {
        try
        {
            NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        }
        catch (Exception)
        {
        }
    }
}
