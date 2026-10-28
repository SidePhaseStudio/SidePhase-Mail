using System.Text.RegularExpressions;

namespace SidePhase.Mail.Core.Formatting;

/// <summary>
/// Removes secrets and personal details from text before it's written to a diagnostic log: OAuth tokens, bearer
/// headers, passwords and authorisation codes in URLs or messages, and the user part of email addresses.
/// </summary>
public static partial class LogRedactor
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = BearerRegex().Replace(text, "$1[redacted]");
        result = SaslRegex().Replace(result, "$1[redacted]");
        result = SecretParameterRegex().Replace(result, "$1=[redacted]");
        result = JsonSecretRegex().Replace(result, "\"$1\":\"[redacted]\"");
        result = JwtRegex().Replace(result, "[redacted-token]");
        result = EmailRegex().Replace(result, m => "***@" + m.Groups[1].Value);
        return result;
    }

    // SASL exchanges as they appear in protocol traces or server replies: "AUTHENTICATE PLAIN <base64>", "AUTH LOGIN <base64>".
    [GeneratedRegex(@"(\b(?:AUTHENTICATE|AUTH)\s+(?:PLAIN|LOGIN|XOAUTH2|OAUTHBEARER|CRAM-MD5|SCRAM-SHA-\d+(?:-PLUS)?|NTLM)\s+)[A-Za-z0-9+/=]{4,}", RegexOptions.IgnoreCase)]
    private static partial Regex SaslRegex();

    [GeneratedRegex(@"(\b(?:Bearer|Basic|XOAUTH2)\s+)[A-Za-z0-9\-._~+/=]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\b(access_token|refresh_token|id_token|client_secret|password|passwd|pwd|code|code_verifier|token|secret|apikey|api_key)\s*[=:]\s*[^\s&""',;]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretParameterRegex();

    [GeneratedRegex(@"""(access_token|refresh_token|id_token|client_secret|password|token|secret)""\s*:\s*""[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecretRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@([A-Za-z0-9.\-]+\.[A-Za-z]{2,})\b")]
    private static partial Regex EmailRegex();
}
