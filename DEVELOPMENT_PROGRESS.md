# Development progress

Legend: ✅ implemented · 🧪 implemented and covered by automated tests · 🟡 partial / demo-only · ⏳ planned

Automated tests: `tests/SidePhase.Mail.Tests` (110 xUnit tests, run in CI on every push).
UI behaviour is verified manually on Windows; there are no automated UI tests yet.

## Milestone 1 — Working application

| Feature | Status | Notes |
| --- | --- | --- |
| Solution structure (App / Core / Infrastructure / Tests) | ✅ | `SidePhase.Mail.sln`, .NET 10, central `Directory.Build.props`. |
| WinUI 3 app (Windows App SDK 2.5, unpackaged, self-contained) | ✅ | Runs from the build folder; no MSIX or runtime installer. |
| MVVM + dependency injection | ✅ | CommunityToolkit.Mvvm partial properties; composition root in `App.xaml.cs`. |
| Dark Glass design system | ✅ | `Themes/Palette.xaml`, `Themes/Styles.xaml`, documented in `docs/DESIGN_SYSTEM.md`. |
| Mica / Acrylic backdrops with fallbacks | ✅ | Solid when transparency is off, in contrast themes or with Solid themes. |
| Themes: Dark Glass, Light Glass, Solid Dark, Solid Light, Follow Windows | ✅ | Dark Glass is the default and most polished. |
| Accent colours | 🧪 | Nine presets; shades derived with WCAG contrast checks (`ColorMath`). |
| Custom title bar with sidebar toggle | ✅ | Native `TitleBar` control, Mica-aware caption buttons. |
| Sidebar navigation | ✅ | Unified Inbox, All Accounts, Pinned, Favourites, Drafts, Sent, Junk, Archive, Trash, accounts with folders, unread badges, collapsible. |
| Message list | ✅ | Virtualised; avatars, unread dot, attachment/pin icons, star, spam tags, hover quick actions, context menu. |
| Pinned section (collapsible) and date groups | 🧪 | `MessageListBuilder`. |
| Search with operators | 🧪 | `from:`, `to:`, `subject:`, `is:unread/read/starred/pinned`, `has:attachment`, quoted phrases. |
| Filter and sort | 🧪 | All / Unread / Favourites / Attachments; newest, oldest, sender, subject. |
| Comfortable / compact density | ✅ | |
| Reading pane (right or bottom) | ✅ | Header, recipients, account/folder chip, attachments, banners, toolbar. |
| Safe HTML rendering in WebView2 | 🧪 | Sanitiser + CSP tested; WebView2 hardening (no script, request interception, external links) verified manually. |
| Remote image blocking with per-message “Show images” | 🧪 | Detection tested. |
| Compose window | 🟡 | To/Cc/Bcc, rich/plain text, signatures, attachments (picker + drag & drop), drafts, send progress. Sending is simulated in demo mode. |
| Reply / Reply all / Forward | 🧪 | `ReplyBuilder`: subject prefixes, recipient de-duplication, quoting, forwarded attachments. |
| Settings | ✅ | Appearance, Accounts, Mail, Privacy, About. Persisted to `%LOCALAPPDATA%\SidePhase Mail\settings.json`. |
| Welcome screen | 🟡 | Google / Microsoft / other account buttons are disabled and labelled “Milestone 2”. |
| Sample data clearly identified as demonstration content | 🧪 | “(demo)” accounts, DEMO DATA badge, `.example` domains only. |
| Keyboard shortcuts | ✅ | Ctrl+N/R/Shift+R/F/E, F5, Delete, Ctrl+Enter. |
| GitHub Actions Windows build | ✅ | Tests, publish, verification of output, artifact upload. |

## Milestone 2 — Real email accounts (next)

| Feature | Status |
| --- | --- |
| MailKit IMAP synchronisation (folders, UIDs, UIDVALIDITY, CONDSTORE/QRESYNC where available) | ⏳ |
| SMTP sending with MimeKit (HTML + text alternative, attachments) | ⏳ |
| SQLite cache (`Microsoft.Data.Sqlite`) behind `IMailDataSource` | ⏳ |
| Account setup: autoconfig (ISPDB, provider autoconfig/autodiscover, SRV), manual IMAP/SMTP, TLS-only | ⏳ |
| Google OAuth 2.0 (installed-app loopback + PKCE) and Microsoft identity platform (MSAL) | ⏳ |
| Token storage with Windows data protection; re-authentication; account removal | ⏳ |
| Background sync, IDLE, retries, timeouts, cancellation | ⏳ |
| Opening/saving attachments; `cid:` inline images | ⏳ |

## Milestone 3 — Productivity

| Feature | Status | Notes |
| --- | --- | --- |
| Pinned emails | 🟡 | Fully working in the UI (pin/unpin, section, collapse, cross-account). Persistence in SQLite keyed by stable ids is Milestone 2/3; demo pins reset on restart. |
| Favourites (starred messages) | 🟡 | Working in the UI; IMAP `\Flagged` sync in Milestone 2. Favourite folders and contacts ⏳. |
| SpamAssassin detection | 🧪 | Header parsing, classification, threshold, local junk filtering, trusted/blocked senders and domains. |
| Send the Bugger Back! | 🧪 | Block sender (+ optional domain, never shared providers), move to Junk, undo, notification suppression policy. Rejection replies are an unimplemented, confirmation-only extension point (`IRejectionResponder`). |
| Desktop notifications | ⏳ | Policy implemented and tested (`NotificationPolicy`); toast delivery pending. |
| Search improvements (server-side, indexed) | ⏳ | |

## Future

Calendar, contacts, snooze, scheduled send, follow-up reminders, Discord and other services — the
`IMailDataSource` boundary, the scope model and the sidebar's data-driven navigation leave room for these. Not started.

## Known limitations of this preview

* Demonstration data only; nothing is sent or received.
* Pins, stars and other changes to demo messages last until the app closes (settings and sender rules persist).
* The reading pane renders message bodies with theme colours; complex newsletters designed for white
  backgrounds will get a dedicated “original colours” option once real mail arrives.
* No automated UI tests yet.
