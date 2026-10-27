using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Abstractions;

/// <summary>
/// Extension point for a future "Send the Bugger Back!" option that sends a polite rejection reply.
/// </summary>
/// <remarks>
/// NOT IMPLEMENTED. Any implementation must require explicit, per-message user confirmation and must never
/// forge delivery-failure (bounce) messages: fake bounces are a form of backscatter and can harm innocent
/// third parties whose addresses were spoofed. No implementation is registered in Milestone 1.
/// </remarks>
public interface IRejectionResponder
{
    Task<DraftMessage> CreateRejectionDraftAsync(MessageSummary message, CancellationToken cancellationToken = default);
}
