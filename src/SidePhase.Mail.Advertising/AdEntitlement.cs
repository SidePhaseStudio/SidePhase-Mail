namespace SidePhase.Mail.Advertising;

/// <summary>
/// Whether the user has the ad-free (Premium) upgrade. Today this is local only (<see cref="AdvertisingSettings.SimulateAdFree"/>
/// for testing). When the upgrade is sold, an implementation backed by the Microsoft Store add-on (Windows.Services.Store
/// StoreContext) replaces <see cref="LocalAdEntitlement"/>; nothing else changes.
/// </summary>
public interface IAdEntitlement
{
    bool IsAdFree { get; }

    event EventHandler? Changed;
}

public sealed class LocalAdEntitlement(Func<AdvertisingSettings> settings) : IAdEntitlement
{
    public bool IsAdFree => settings().SimulateAdFree;

    public event EventHandler? Changed;

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
