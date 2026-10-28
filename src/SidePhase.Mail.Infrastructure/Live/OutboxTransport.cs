using SidePhase.Mail.Core.Scheduling;
using SidePhase.Mail.Infrastructure.Demo;

namespace SidePhase.Mail.Infrastructure.Live;

/// <summary>
/// Sends outbox items through the right engine: demonstration items through the demo mailbox (nothing leaves the PC),
/// real items through SMTP, whichever mailbox is currently on screen.
/// </summary>
public sealed class OutboxTransport(DemoMailDataSource demo, LiveMailDataSource live) : IOutgoingTransport
{
    public async Task<SendAttemptResult> SendAsync(OutboxItem item, CancellationToken ct)
    {
        var draft = item.Draft with { StableMessageKey = item.Id };
        var result = item.IsDemo
            ? await demo.SendAsync(draft, null, ct).ConfigureAwait(false)
            : await live.SendAsync(draft, null, ct).ConfigureAwait(false);
        return new SendAttemptResult(result.Outcome, result.Message);
    }
}
