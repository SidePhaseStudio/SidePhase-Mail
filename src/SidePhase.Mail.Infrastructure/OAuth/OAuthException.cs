namespace SidePhase.Mail.Infrastructure.OAuth;

/// <summary>An OAuth sign-in or token refresh failed. The message is safe to show to the user.</summary>
public sealed class OAuthException(string message, Exception? inner = null) : Exception(message, inner);
