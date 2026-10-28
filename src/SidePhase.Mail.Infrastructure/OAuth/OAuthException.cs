namespace SidePhase.Mail.Infrastructure.OAuth;

/// <summary>
/// An OAuth sign-in or token refresh failed. The message is safe to show to the user. <see cref="IsTransient"/> marks
/// failures that say nothing about the sign-in itself (provider outage, network, a proxy's error page): those are
/// retried later and never ask the user to sign in again.
/// </summary>
public sealed class OAuthException(string message, Exception? inner = null, bool isTransient = false) : Exception(message, inner)
{
    public bool IsTransient { get; } = isTransient;
}
