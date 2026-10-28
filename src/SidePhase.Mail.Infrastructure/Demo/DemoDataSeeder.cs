using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Rendering;
using SidePhase.Mail.Core.Spam;

namespace SidePhase.Mail.Infrastructure.Demo;

public sealed record DemoData(
    IReadOnlyList<MailAccount> Accounts,
    IReadOnlyList<MailFolder> Folders,
    IReadOnlyList<MessageSummary> Messages,
    IReadOnlyList<MessageBody> Bodies,
    IReadOnlyList<DraftMessage> Drafts);

/// <summary>
/// DEMONSTRATION CONTENT. Every person, company and address below is fictional and uses reserved
/// <c>.example</c> domains (RFC 2606), so nothing can ever be delivered to a real mailbox.
/// Dates are relative to "now" so the inbox always looks current.
/// </summary>
public static class DemoDataSeeder
{
    public static readonly Guid PersonalAccountId = new("5e1d0c3a-0000-4000-8000-000000000001");
    public static readonly Guid WorkAccountId = new("5e1d0c3a-0000-4000-8000-000000000002");

    public static DemoData Create(DateTimeOffset now)
    {
        var builder = new Builder(now);
        builder.Build();
        return new DemoData(builder.Accounts, builder.Folders, builder.Messages, builder.Bodies, builder.Drafts);
    }

    private sealed class Builder(DateTimeOffset now)
    {
        private int _sequence;

        public List<MailAccount> Accounts { get; } = [];
        public List<MailFolder> Folders { get; } = [];
        public List<MessageSummary> Messages { get; } = [];
        public List<MessageBody> Bodies { get; } = [];
        public List<DraftMessage> Drafts { get; } = [];

        private static readonly EmailAddress MePersonal = new("Alex Morgan", "alex.morgan@sidephase-demo.example");
        private static readonly EmailAddress MeWork = new("Alex Morgan", "alex@northwind-studio.example");

        public void Build()
        {
            Accounts.Add(new MailAccount
            {
                Id = PersonalAccountId,
                DisplayName = "Personal (demo)",
                EmailAddress = MePersonal.Address,
                Provider = MailProviderKind.Demo,
                ColorHex = "#8B7CFF",
            });
            Accounts.Add(new MailAccount
            {
                Id = WorkAccountId,
                DisplayName = "Northwind Studio (demo)",
                EmailAddress = MeWork.Address,
                Provider = MailProviderKind.Demo,
                ColorHex = "#3FB6A8",
            });

            AddStandardFolders(PersonalAccountId, "Travel", "Receipts");
            AddStandardFolders(WorkAccountId, "Projects", "Clients");

            SeedPersonal();
            SeedWork();
        }

        private void AddStandardFolders(Guid accountId, params string[] custom)
        {
            var order = 0;
            foreach (var (name, kind) in new[]
                     {
                         ("Inbox", FolderKind.Inbox), ("Drafts", FolderKind.Drafts), ("Sent", FolderKind.Sent),
                         ("Archive", FolderKind.Archive), ("Junk", FolderKind.Junk), ("Trash", FolderKind.Trash),
                     })
            {
                Folders.Add(new MailFolder
                {
                    Id = FolderId(accountId, order),
                    AccountId = accountId,
                    Name = name,
                    Path = name == "Inbox" ? "INBOX" : name,
                    Kind = kind,
                    SortOrder = order++,
                });
            }

            foreach (var name in custom)
            {
                Folders.Add(new MailFolder
                {
                    Id = FolderId(accountId, order),
                    AccountId = accountId,
                    Name = name,
                    Path = name,
                    Kind = FolderKind.Custom,
                    SortOrder = order++,
                });
            }
        }

        private static Guid FolderId(Guid accountId, int index)
        {
            var bytes = accountId.ToByteArray();
            bytes[15] = (byte)(0x80 + index);
            return new Guid(bytes);
        }

        private MailFolder Folder(Guid accountId, string name) =>
            Folders.First(f => f.AccountId == accountId && f.Name == name);

        private void SeedPersonal()
        {
            var a = PersonalAccountId;
            var me = MePersonal;

            Add(a, "Inbox", P("Priya Shah", "priya.shah@lumen-design.example"), [me],
                "Lisbon itinerary — final version",
                Ago(minutes: 34), read: false, starred: true, pinned: true, pinnedHoursAgo: 20,
                html: """
                <p>Hi Alex,</p>
                <p>Here's the final itinerary for Lisbon. I've moved the Sintra day trip to Saturday so we can catch the
                sunset at Cabo da Roca, and booked the tram tour for Friday morning.</p>
                <ul>
                  <li><b>Thu 14 Nov</b> — arrive 16:20, check in at Casa Alfama</li>
                  <li><b>Fri 15 Nov</b> — Tram 28 tour, LX Factory in the afternoon</li>
                  <li><b>Sat 16 Nov</b> — Sintra &amp; Cabo da Roca</li>
                  <li><b>Sun 17 Nov</b> — Belém, pastéis, slow evening</li>
                </ul>
                <p>The PDF has addresses and booking references. Let me know if anything looks off!</p>
                <p>Priya</p>
                """,
                attachments: [new("Lisbon-itinerary.pdf", "application/pdf", 482_113), new("Casa-Alfama-booking.pdf", "application/pdf", 128_400)]);

            Add(a, "Inbox", P("Sam Okafor", "sam.okafor@sidephase-demo.example"), [me, P("Theo Lambert", "theo@lambert-home.example")],
                "Saturday 5-a-side?",
                Ago(hours: 2, minutes: 10), read: false,
                text: """
                Alright lads,

                Pitch is booked for 10:30 on Saturday at Riverside. We're one short — Alex, you in?
                Bring the light shirts, the other lot are wearing dark.

                Sam
                """);

            Add(a, "Inbox", P("Northline Bank", "alerts@northline-bank.example"), [me],
                "Your October statement is ready",
                Ago(hours: 3, minutes: 45), read: true,
                html: """
                <p>Hello Alex,</p>
                <p>Your statement for the account ending <b>••42</b> is ready to view in the Northline app.</p>
                <p class="sp-muted">For your security we never include links to sign in. Open the app directly.</p>
                """,
                spam: Spam("No, score=-1.2 required=5.0 tests=DKIM_SIGNED,DKIM_VALID,SPF_PASS autolearn=ham version=3.4.6"));

            Add(a, "Inbox", P("Mum", "helen.morgan@morgan-family.example"), [me],
                "Photos from the weekend 📷",
                Ago(days: 1, hours: 2), read: true, starred: true,
                html: """
                <p>Hello love,</p>
                <p>Here are the photos from Sunday. The one of Biscuit in the leaves is my favourite —
                I might frame it for your nan.</p>
                <p>Dad says thank you for fixing the router. It's been working perfectly since!</p>
                <p>Lots of love,<br>Mum xx</p>
                """,
                attachments: [new("biscuit-autumn.jpg", "image/jpeg", 2_412_901), new("garden-lunch.jpg", "image/jpeg", 1_934_220), new("sunset-walk.jpg", "image/jpeg", 2_086_013)]);

            Add(a, "Inbox", P("Fieldnote Weekly", "hello@fieldnote-weekly.example"), [me],
                "Issue 142: The quiet art of good defaults",
                Ago(days: 1, hours: 6), read: true,
                html: """
                <div style="max-width:620px">
                  <img src="https://images.fieldnote-weekly.example/header-142.png" alt="Fieldnote Weekly" width="620">
                  <h2>The quiet art of good defaults</h2>
                  <p>The best software decisions are the ones users never have to make. This week we look at three
                  apps that get defaults right — and one that very much doesn't.</p>
                  <p><a href="https://fieldnote-weekly.example/142">Read the full issue →</a></p>
                  <img src="https://track.fieldnote-weekly.example/open.gif?u=8812" width="1" height="1" alt="">
                  <p class="sp-muted">You're receiving this because you subscribed at fieldnote-weekly.example.</p>
                </div>
                """);

            Add(a, "Inbox", P("Security Team", "no-reply@secure-login-check.example"), [me],
                "Action required: verify your account details",
                Ago(days: 2, hours: 1), read: false,
                html: """
                <p>Dear customer,</p>
                <p>We noticed unusual activity. Please verify your details within 24 hours to avoid suspension.</p>
                <p><a href="https://secure-login-check.example/verify">Verify now</a></p>
                """,
                spam: Spam("No, score=3.9 required=5.0 tests=HTML_MESSAGE,URI_PHISH,FREEMAIL_FROM,MISSING_MID autolearn=no version=3.4.6"));

            Add(a, "Inbox", P("Parcelway", "tracking@parcelway.example"), [me],
                "Your parcel is out for delivery",
                Ago(days: 2, hours: 5), read: true,
                html: """
                <p>Good news — parcel <b>PW-55821-UK</b> is out for delivery today between <b>13:00 and 15:00</b>.</p>
                <p>If you're not in, we'll leave it in your nominated safe place: <i>the blue bin store</i>.</p>
                """);

            Add(a, "Inbox", P("Rewards Centre", "winner@prize-alerts.example"), [me],
                "CONGRATULATIONS!!! Claim your £5,000 reward now",
                Ago(days: 3), read: false,
                html: """
                <p style="font-size:22px"><b>You have been SELECTED!</b></p>
                <p>Click below in the next 10 minutes to claim your exclusive reward.</p>
                <p><a href="https://prize-alerts.example/claim">CLAIM NOW</a></p>
                """,
                spam: Spam("Yes, score=14.2 required=5.0 tests=BAYES_99,HTML_IMAGE_ONLY_16,LOTS_OF_MONEY,URIBL_BLACK,FREEMAIL_FORGED_REPLYTO autolearn=spam version=3.4.6",
                    ("X-Spam-Flag", "YES"), ("X-Spam-Level", "**************")));

            Add(a, "Inbox", P("Theo Lambert", "theo@lambert-home.example"), [me],
                "Re: Flat viewing on Thursday",
                Ago(days: 3, hours: 4), read: true,
                text: """
                Thursday at 6 works for me. The agent said the second bedroom gets the evening sun,
                so it should look its best around then.

                I'll meet you outside the café on the corner.

                Theo

                > On Mon, Alex Morgan wrote:
                > Are you free for the viewing on Thursday? Any time after 5.
                """);

            Add(a, "Inbox", P("Brightsmile Dental", "appointments@brightsmile.example"), [me],
                "Reminder: check-up on Monday at 09:15",
                Ago(days: 5), read: true,
                html: """
                <p>Hi Alex, this is a reminder of your check-up with Dr. Ellis on <b>Monday at 09:15</b>.</p>
                <p>Need to rearrange? Reply to this email or call the practice.</p>
                """);

            Add(a, "Inbox", P("Lumen Air", "billing@lumen-air.example"), [me],
                "Your annual subscription has renewed",
                Ago(days: 11), read: true,
                html: """
                <p>Thanks for staying with Lumen Air. Your annual plan has renewed for another year.</p>
                <table style="border-collapse:collapse">
                  <tr><td style="padding:4px 16px 4px 0">Plan</td><td><b>Lumen Air Family</b></td></tr>
                  <tr><td style="padding:4px 16px 4px 0">Amount</td><td><b>£59.99</b></td></tr>
                  <tr><td style="padding:4px 16px 4px 0">Next renewal</td><td>in 12 months</td></tr>
                </table>
                """,
                attachments: [new("Receipt-LA-20931.pdf", "application/pdf", 64_220)]);

            Add(a, "Travel", P("Skylark Airways", "bookings@skylark-air.example"), [me],
                "Booking confirmed: London → Lisbon, 14 Nov",
                Ago(days: 9), read: true, starred: true,
                html: """
                <h3>You're going to Lisbon ✈️</h3>
                <p>Booking reference <b>SKL7Q2</b> · 2 passengers</p>
                <p>Outbound: Thu 14 Nov, LGW 13:05 → LIS 16:20<br>Return: Mon 18 Nov, LIS 17:40 → LGW 20:10</p>
                <p class="sp-muted">Online check-in opens 48 hours before departure.</p>
                """,
                attachments: [new("SKL7Q2-e-ticket.pdf", "application/pdf", 210_884)]);

            Add(a, "Receipts", P("Corner Books", "orders@cornerbooks.example"), [me],
                "Order #20417 has been dispatched",
                Ago(days: 16), read: true,
                text: "Your order of 'The Design of Everyday Things' has been dispatched and should arrive in 2–3 days.");

            Add(a, "Sent", me, [P("Sam Okafor", "sam.okafor@sidephase-demo.example")],
                "Re: Saturday 5-a-side?",
                Ago(hours: 1, minutes: 50), read: true,
                text: "Count me in! I'll bring the spare ball.\n\nAlex");

            Add(a, "Sent", me, [P("Priya Shah", "priya.shah@lumen-design.example")],
                "Restaurant ideas for Lisbon",
                Ago(days: 4), read: true,
                text: "A few ideas from Mia: Taberna da Rua das Flores, Cervejaria Ramiro (for the seafood) and Manteigaria for the best pastéis.");

            Add(a, "Archive", P("Hearth Energy", "hello@hearth-energy.example"), [me],
                "Your meter reading has been received",
                Ago(days: 21), read: true,
                text: "Thanks — we've received your meter reading. Your next bill will use it.");

            Add(a, "Junk", P("Crypto Profits Daily", "deals@quick-crypto.example"), [me],
                "Turn £250 into £25,000 — guaranteed",
                Ago(days: 1, hours: 9), read: false,
                text: "Our members are making thousands every single day. Limited places available!",
                spam: Spam("Yes, score=18.7 required=5.0 tests=BAYES_99,MONEY_FRAUD_8,URIBL_DBL_SPAM version=3.4.6", ("X-Spam-Flag", "YES")));

            Add(a, "Trash", P("Gym Republic", "news@gym-republic.example"), [me],
                "Last chance: 50% off annual memberships",
                Ago(days: 6), read: true,
                text: "Our autumn offer ends at midnight.");

            AddDraft(a, me, [P("Priya Shah", "priya.shah@lumen-design.example")], "Ideas for Dad's 60th",
                "Thinking we could do a surprise lunch at the boathouse, then a walk along the river if the weather holds. Thoughts?",
                Ago(hours: 5));
        }

        private void SeedWork()
        {
            var a = WorkAccountId;
            var me = MeWork;
            var jordan = P("Jordan Reyes", "jordan.reyes@northwind-studio.example");
            var mei = P("Mei Tanaka", "mei.tanaka@northwind-studio.example");
            var ravi = P("Ravi Patel", "ravi.patel@northwind-studio.example");

            Add(a, "Inbox", jordan, [me, mei], "Q4 roadmap review — slides attached",
                Ago(minutes: 12), read: false, pinned: true, pinnedHoursAgo: 2, cc: [ravi],
                html: """
                <p>Morning both,</p>
                <p>Slides for Thursday's roadmap review are attached. The big three for Q4:</p>
                <ol>
                  <li><b>Offline sync</b> — the cache layer and conflict handling</li>
                  <li><b>Accessibility pass</b> — keyboard navigation and screen reader labels everywhere</li>
                  <li><b>Performance</b> — cold start under 800 ms on the reference laptop</li>
                </ol>
                <p>Could you each add your estimates to slide 7 before Wednesday lunchtime?</p>
                <p>Thanks,<br>Jordan</p>
                """,
                attachments: [new("Q4-Roadmap-Review.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", 2_481_337)]);

            Add(a, "Inbox", mei, [me], "Design review notes: sidebar & message list",
                Ago(minutes: 48), read: false, starred: true,
                html: """
                <p>Hi Alex,</p>
                <p>Great session today. Summary of what we agreed:</p>
                <ul>
                  <li>Sidebar stays translucent; selection uses the accent at low opacity.</li>
                  <li>Unread rows get a small accent dot and a semibold sender — no bold subjects.</li>
                  <li>Quick actions appear on hover only, so the list stays calm.</li>
                  <li>Compact density hides the preview line.</li>
                </ul>
                <blockquote>"Luxurious without being flashy" — let's keep that as the north star.</blockquote>
                <p>Mei</p>
                """);

            Add(a, "Inbox", P("Olivia Grant", "olivia.grant@harbourlight.example"), [me], "Contract renewal – a few questions",
                Ago(hours: 6), read: false,
                html: """
                <p>Hi Alex,</p>
                <p>Thanks for sending the renewal over. Before we sign, could you clarify:</p>
                <ol>
                  <li>Whether the support hours in section 4.2 include UK bank holidays</li>
                  <li>The notice period if we add a second site mid-term</li>
                </ol>
                <p>I've attached a marked-up copy. Happy to jump on a call Thursday if that's easier.</p>
                <p>Best,<br>Olivia Grant<br><span class="sp-muted">Operations Director, Harbourlight</span></p>
                """,
                attachments: [new("Harbourlight-renewal-v3-markup.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", 318_904)]);

            Add(a, "Inbox", P("Build Bot", "ci@northwind-studio.example"), [me], "✅ Release 2.3.0 passed all checks",
                Ago(hours: 4, minutes: 20), read: true,
                text: """
                Pipeline #4817 succeeded.

                  ✔ Restore      18s
                  ✔ Build        1m 42s
                  ✔ Unit tests   312 passed
                  ✔ Package      54s

                Artifacts are available for 30 days.
                """);

            Add(a, "Inbox", P("People Team", "people@northwind-studio.example"), [me], "Reminder: timesheets due Friday",
                Ago(days: 1, hours: 3), read: true,
                text: "A friendly reminder to submit this month's timesheet by Friday at 17:00. Thank you!");

            Add(a, "Inbox", ravi, [me, jordan, mei], "Lunch & learn: motion in Fluent design",
                Ago(days: 2, hours: 2), read: true,
                html: """
                <p>Hi all — next Wednesday I'm running a 30-minute lunch &amp; learn on motion: connected animations,
                easing curves and when <i>not</i> to animate. Pizza is on the studio. 🍕</p>
                <p>Ravi</p>
                """);

            Add(a, "Inbox", P("Design Systems Digest", "digest@designsystems-digest.example"), [me], "Tokens, themes and the dark-mode trap",
                Ago(days: 6), read: true,
                html: """
                <img src="https://cdn.designsystems-digest.example/banner.jpg" alt="Design Systems Digest" width="600">
                <h2>Tokens, themes and the dark-mode trap</h2>
                <p>Pure black backgrounds look striking in a mock-up and exhausting after an hour. We compare how
                four teams built their dark palettes from a single set of tokens.</p>
                <img src="https://cdn.designsystems-digest.example/pixel.gif?id=77" width="1" height="1" alt="">
                """);

            Add(a, "Inbox", P("Finance", "finance@northwind-studio.example"), [me], "Expense claim EXP-2291 approved",
                Ago(days: 4, hours: 1), read: true,
                text: "Your expense claim EXP-2291 (£86.40, client travel) has been approved and will be paid on the next payroll run.");

            Add(a, "Inbox", P("Ellie Brooks", "ellie.brooks@northwind-studio.example"), [me], "Welcome aboard! Your first-week plan",
                Ago(days: 8), read: true, pinned: true, pinnedHoursAgo: 190,
                html: """
                <p>Welcome to Northwind, Alex! 🎉</p>
                <p>Your first week:</p>
                <ul>
                  <li><b>Monday</b> — laptop setup and a studio tour</li>
                  <li><b>Tuesday</b> — meet the mail client team</li>
                  <li><b>Wednesday</b> — codebase walkthrough with Jordan</li>
                  <li><b>Friday</b> — team lunch</li>
                </ul>
                <p>The handbook is attached. Shout if you need anything.</p>
                """,
                attachments: [new("Northwind-Handbook-2026.pdf", "application/pdf", 3_904_118)]);

            Add(a, "Inbox", P("Facilities", "facilities@northwind-studio.example"), [me], "Studio closed on Friday for maintenance",
                Ago(days: 10), read: true,
                text: "The studio will be closed this Friday while the lifts are serviced. Please work from home.");

            Add(a, "Projects", jordan, [me], "Kick-off: Harbourlight website refresh",
                Ago(days: 15), read: true,
                text: "Kick-off is booked for the 3rd. Agenda, scope and the first draft of the timeline are in the shared folder.");

            Add(a, "Clients", P("Olivia Grant", "olivia.grant@harbourlight.example"), [me], "Thank you for a great quarter",
                Ago(days: 28), read: true,
                text: "Just wanted to say thank you to the whole team — the new booking flow has cut our support calls by a third.");

            Add(a, "Sent", me, [mei], "Re: Design review notes: sidebar & message list",
                Ago(minutes: 30), read: true,
                text: "Perfect summary, thanks Mei. I'll update the design tokens this afternoon and share a build tomorrow.");

            Add(a, "Archive", jordan, [me], "Sprint 41 retro notes",
                Ago(days: 18), read: true,
                text: "Went well: the release train. To improve: fewer late scope changes. Action: Jordan to trial a scope freeze on Wednesdays.");

            Add(a, "Junk", P("SEO Experts", "rank@seo-number-one.example"), [me], "We can get Northwind to #1 on every search engine",
                Ago(days: 2, hours: 8), read: false,
                text: "Guaranteed first-page ranking in 7 days or your money back!!!",
                spam: Spam("Yes, score=9.8 required=5.0 tests=BAYES_95,GUARANTEED_100_PERCENT,URIBL_ABUSE_SURBL version=3.4.6", ("X-Spam-Flag", "YES")));

            Add(a, "Trash", P("Calendar", "calendar@northwind-studio.example"), [me], "Cancelled: Weekly sync",
                Ago(days: 3), read: true,
                text: "This event has been cancelled by the organiser.");

            AddDraft(a, me, [jordan], "Proposal: offline sync strategy",
                "Rough outline: cache headers and bodies separately, use CONDSTORE/QRESYNC where the server supports it, and fall back to UID ranges otherwise.",
                Ago(days: 1));
        }

        private void Add(
            Guid accountId,
            string folderName,
            EmailAddress from,
            IReadOnlyList<EmailAddress> to,
            string subject,
            DateTimeOffset date,
            bool read,
            bool starred = false,
            bool pinned = false,
            int pinnedHoursAgo = 0,
            IReadOnlyList<EmailAddress>? cc = null,
            string? html = null,
            string? text = null,
            IReadOnlyList<AttachmentInfo>? attachments = null,
            SpamReport? spam = null)
        {
            var folder = Folder(accountId, folderName);
            var id = NextId();
            var plain = text ?? HtmlText.ToPlainText(html) ?? string.Empty;
            Messages.Add(new MessageSummary
            {
                Id = id,
                AccountId = accountId,
                FolderId = folder.Id,
                FolderKind = folder.Kind,
                MessageIdHeader = $"<demo-{id:N}@sidephase-demo.example>",
                From = from,
                To = to,
                Cc = cc ?? [],
                Subject = subject,
                Preview = HtmlText.Preview(plain),
                Date = date,
                IsRead = read,
                IsStarred = starred,
                IsPinned = pinned,
                PinnedAt = pinned ? now.AddHours(-pinnedHoursAgo) : null,
                HasAttachments = attachments is { Count: > 0 },
                Attachments = attachments ?? [],
                Spam = spam ?? SpamReport.None,
                Size = 2048 + (html?.Length ?? 0) + plain.Length + (attachments?.Sum(a => a.SizeBytes * 4 / 3) ?? 0),
            });
            Bodies.Add(new MessageBody
            {
                MessageId = id,
                HtmlBody = html,
                TextBody = plain,
                Attachments = attachments ?? [],
            });
        }

        private void AddDraft(Guid accountId, EmailAddress me, IReadOnlyList<EmailAddress> to, string subject, string body, DateTimeOffset date)
        {
            var folder = Folder(accountId, "Drafts");
            var id = NextId();
            Messages.Add(new MessageSummary
            {
                Id = id,
                AccountId = accountId,
                FolderId = folder.Id,
                FolderKind = FolderKind.Drafts,
                From = me,
                To = to,
                Subject = subject,
                Preview = HtmlText.Preview(body),
                Date = date,
                IsRead = true,
            });
            Bodies.Add(new MessageBody { MessageId = id, TextBody = body });
            Drafts.Add(new DraftMessage
            {
                DraftId = id,
                AccountId = accountId,
                To = to,
                Subject = subject,
                TextBody = body,
                IsPlainText = false,
            });
        }

        private Guid NextId()
        {
            _sequence++;
            return new Guid($"de300000-0000-4000-8000-{_sequence:D12}");
        }

        private DateTimeOffset Ago(int days = 0, int hours = 0, int minutes = 0) =>
            now - new TimeSpan(days, hours, minutes, 0);

        private static EmailAddress P(string name, string address) => new(name, address);

        private static SpamReport Spam(string status, params (string Name, string Value)[] extra)
        {
            var headers = new List<KeyValuePair<string, string>> { new(SpamAssassinHeaderParser.StatusHeader, status) };
            headers.AddRange(extra.Select(e => new KeyValuePair<string, string>(e.Name, e.Value)));
            return SpamAssassinHeaderParser.Parse(headers);
        }
    }
}
