# SidePhase Mail

**A premium, native Windows 11 email client by SidePhase Studio.**

SidePhase Mail is a genuine WinUI 3 desktop application — not a web wrapper — with a distinctive
*Dark Glass* design built on Microsoft's Fluent Design: Mica backdrops, translucent layers, thin
hairline borders, Segoe UI Variable typography and a calm, refined accent colour.

> **Status: Milestone 2 preview.** Real IMAP/SMTP accounts work: add an account, read, reply, send,
> organise folders, pin and star — all cached locally in SQLite. Google and Microsoft sign-in (OAuth) are
> implemented but **not yet verified with real accounts**; they need app registrations
> ([docs/ACCOUNT_PROVIDERS.md](docs/ACCOUNT_PROVIDERS.md)). The demonstration mailbox is still available and never
> mixes with real mail. See [DEVELOPMENT_PROGRESS.md](DEVELOPMENT_PROGRESS.md) for exactly what is implemented and tested.

## Adding an account

*Add account* (sidebar) → choose Google, Microsoft or *another email account* → enter your address. SidePhase Mail
finds the server settings, signs in (OAuth in your browser, or a password / app password), tests incoming and outgoing
mail, and only then saves the account. Passwords and tokens are encrypted with Windows and never leave your PC except
to your own provider.

## Download and run

1. Open the repository's **Actions** tab → latest successful **Build** run.
2. Download the **SidePhaseMail-win-x64** artifact and extract it.
3. Double-click **SidePhaseMail.exe**.

No installer is required: .NET 10 and the Windows App SDK are bundled (self-contained, unpackaged).
You need Windows 10 1809 or later (Windows 11 recommended for Mica) and the Microsoft Edge WebView2
Runtime, which is pre-installed on Windows 11. The preview is unsigned, so SmartScreen may ask you
to confirm (*More info → Run anyway*).

To build it yourself see **[BUILD.md](BUILD.md)**.

## Highlights

| Area | What you get in Milestone 1 |
| --- | --- |
| **Dark Glass** | Mica (or Acrylic on Windows 10) with palette-tinted layers; automatic solid fallback when transparency effects are off or a contrast theme is on. Light Glass, Solid Dark, Solid Light and Follow Windows themes. Nine accent colours, contrast-checked. |
| **Sidebar** | Collapsible glass navigation: Unified Inbox, All Accounts, Pinned, Favourites, Drafts, Sent, Junk, Archive, Trash, then each account with expandable folders and unread badges. Add Account and Settings at the bottom. |
| **Message list** | Virtualised list with avatars, unread dot, attachment/pin icons, star, spam tags, date grouping (Today, Yesterday…), a collapsible **Pinned** section, hover quick actions, a full right-click menu, search (with `from:`, `subject:`, `is:unread`, `has:attachment`…), filter, sort and comfortable/compact density. |
| **Reading pane** | Spacious header, recipients, account/folder chip, attachment cards, spam and remote-image banners, and a locked-down WebView2 renderer (no scripts, no remote content unless you allow it, links open in your browser). Right or bottom position. |
| **Compose** | Separate glass window: From, To/Cc/Bcc, subject, rich text (bold/italic/underline/bullets) or plain text, signatures, attachments with drag-and-drop, draft saving and send progress. |
| **Pinned & Favourites** | Pin to Top / Unpin, star / unstar, across all accounts — independent of each other. |
| **SpamAssassin** | Parses `X-Spam-Status`, `X-Spam-Flag`, `X-Spam-Score`, `X-Spam-Level`; configurable threshold; local junk filtering that never deletes anything; trusted senders, blocked senders and blocked domains. |
| **Send the Bugger Back!** | Right-click any message: blocks the sender (optionally the whole domain — never for shared providers like gmail.com), moves it to Junk and silences it. Fully undoable. Nothing is ever sent to the sender. |
| **Settings** | Appearance, Accounts (signatures, sync interval), Mail (pins, spam, sender lists), Privacy (remote images, local data), About. |

## Architecture

```
SidePhase.Mail.sln
├─ src/SidePhase.Mail.Core            UI-independent domain: models, rules, services (net10.0)
├─ src/SidePhase.Mail.Infrastructure  MailKit IMAP/SMTP engine, SQLite cache, secret store, OAuth, account setup, demo data (net10.0)
├─ src/SidePhase.Mail.App             WinUI 3 app: views, view models (MVVM), theme, windows (net10.0-windows)
└─ tests/SidePhase.Mail.Tests         xUnit tests for Core and Infrastructure (run on any OS)
```

* **The email engine is independent of the UI.** View models talk only to `IMailDataSource`
  (`SidePhase.Mail.Core.Abstractions`). `MailDataSourceRouter` serves either `LiveMailDataSource` (SQLite cache +
  MailKit) or `DemoMailDataSource`, never both.
* **MVVM + DI**: CommunityToolkit.Mvvm view models, `Microsoft.Extensions.DependencyInjection`
  composition root in `App.xaml.cs`.
* **Stable message identity**: messages carry a stable local id plus the RFC 5322 `Message-ID`, never
  IMAP sequence numbers, so pins and favourites survive moves and resynchronisation.
* **Lazy bodies**: lists only ever hold lightweight summaries; bodies load on demand with cancellation.
* **Security**: HTML email is sanitised, served with a strict Content-Security-Policy and rendered with
  scripts, host objects, dev tools, downloads and navigation disabled. Remote images are blocked by default.
  Passwords and tokens are encrypted with Windows DPAPI and never stored in SQLite, logs or settings; connections are
  TLS-only (see [docs/ACCOUNT_PROVIDERS.md](docs/ACCOUNT_PROVIDERS.md)).

Design tokens and the visual language are documented in [docs/DESIGN_SYSTEM.md](docs/DESIGN_SYSTEM.md).

## Keyboard shortcuts

| Keys | Action |
| --- | --- |
| Ctrl+N | New message |
| Ctrl+R / Ctrl+Shift+R | Reply / Reply all |
| Ctrl+F | Forward |
| Ctrl+E | Search |
| F5 | Check for new mail |
| Delete | Delete selected message |
| Ctrl+Enter | Send (compose window) |

## Demonstration data

Every person, company and address in the demo is fictional and uses reserved `.example` domains, so
nothing can ever reach a real mailbox. Accounts are labelled “(demo)”, the title bar shows **DEMO DATA**,
and sending only places the message in Sent. *Settings → Privacy → Clear local data* resets the sample mailbox.

## Licence

MIT — see [LICENSE](LICENSE).
