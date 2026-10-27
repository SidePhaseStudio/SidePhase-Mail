namespace SidePhase.Mail.Core.Models;

/// <summary>
/// A mailbox address with an optional display name.
/// </summary>
public sealed record EmailAddress(string? Name, string Address)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Address : Name!.Trim();

    public string Domain
    {
        get
        {
            var at = Address.LastIndexOf('@');
            return at >= 0 && at < Address.Length - 1
                ? Address[(at + 1)..].Trim().ToLowerInvariant()
                : string.Empty;
        }
    }

    public string NormalizedAddress => Address.Trim().ToLowerInvariant();

    public bool IsSameMailbox(EmailAddress other) =>
        string.Equals(NormalizedAddress, other.NormalizedAddress, StringComparison.Ordinal);

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
}
