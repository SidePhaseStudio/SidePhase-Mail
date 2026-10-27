namespace SidePhase.Mail.Core.Models;

/// <summary>How a connection is secured. There is deliberately no "none" or "opportunistic" option.</summary>
public enum ConnectionSecurity
{
    /// <summary>TLS from the first byte (IMAPS 993, SMTPS 465).</summary>
    SslOnConnect,

    /// <summary>Plain connection upgraded with STARTTLS; the connection fails if the server can't upgrade.</summary>
    StartTls,
}

public enum AuthMethod
{
    Password,
    OAuthGoogle,
    OAuthMicrosoft,
}

public sealed record ServerEndpoint(string Host, int Port, ConnectionSecurity Security)
{
    public override string ToString() => $"{Host}:{Port} ({(Security == ConnectionSecurity.SslOnConnect ? "SSL/TLS" : "STARTTLS")})";
}

/// <summary>Everything needed to reach an account's servers, except the secret itself.</summary>
public sealed record AccountConnectionSettings
{
    public required ServerEndpoint Imap { get; init; }
    public required ServerEndpoint Smtp { get; init; }
    public required string Username { get; init; }
    public required AuthMethod AuthMethod { get; init; }
}
