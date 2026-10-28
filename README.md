# SidePhase Mail

**A premium, native Windows 11 email client by SidePhase Studio.**

SidePhase Mail is a genuine WinUI 3 desktop application — not a web wrapper — with a distinctive
*Dark Glass* design built on Microsoft's Fluent Design: Mica backdrops, translucent layers, thin
hairline borders, Segoe UI Variable typography and a calm, refined accent colour.

> **Status: 0.6 beta (Milestone 6), not production-ready.** Real IMAP/SMTP accounts work and are tested end to end;
> Google and Microsoft sign-in (OAuth) are implemented but **not yet verified with real accounts**; they need app
> registrations and provider verification ([docs/ACCOUNT_PROVIDERS.md](docs/ACCOUNT_PROVIDERS.md)).
> [RELEASE_READINESS.md](RELEASE_READINESS.md) says exactly what is verified, what isn't, and the known issues;
> [docs/BETA_TESTING.md](docs/BETA_TESTING.md) is the tester checklist.

## Adding an account

*Add account* (sidebar) → choose Google, Microsoft or *another email account* → enter your address. SidePhase Mail
finds the server settings, signs in (OAuth in your browser, or a password / app password), tests incoming and outgoing
mail, and only then saves the account. Passwords and tokens are encrypted with Windows and never leave your PC except
to your own provider.

## Download and run

Each **Build** run (Actions tab) produces two downloads:

* **`SidePhaseMail-<version>-win-x64-msix`**: the installed app (Start menu, uninstall, default email app). Beta
  packages are unsigned: run the included `Install-DevPackage.ps1` from an administrator PowerShell (it signs the
  package with a test certificate created on your PC only). See [docs/PACKAGING.md](docs/PACKAGING.md).
* **`SidePhaseMail-<version>-win-x64-portable`**: extract and double-click `SidePhaseMail.exe`. No installer;
  .NET 10 and the Windows App SDK are bundled. SmartScreen may ask you to confirm (*More info → Run anyway*).

Windows 11 x64 is the target (Windows 10 2004+ works). The Microsoft Edge WebView2 Runtime is part of Windows 11.
The installed app copies your accounts and settings from the portable build the first time it starts.

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
| **Settings** | Appearance, Accounts (signatures, sync interval), Mail (pins, spam, sender lists), Privacy (remote images, local data), About. |

## Architecture

```
SidePhase.Mail.sln
├─ src/SidePhase.Mail.Core            UI-independent domain: models, rules, services (net10.0)
├─ src/SidePhase.Mail.Infrastructure  MailKit IMAP/SMTP engine, SQLite cache, secret store, OAuth, account setup, demo data (net10.0)
├─ src/SidePhase.Mail.Advertising     Optional sponsored cards, separate from the mail engine (net10.0; off by default)
├─ src/SidePhase.Mail.App             WinUI 3 app: views, view models (MVVM), theme, windows (net10.0-windows); MSIX manifest
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
| Ctrl+Shift+F | Forward |
| Ctrl+F (or Ctrl+E) | Search |
| F5 | Check for new mail |
| Delete | Move selected message (or conversation) to Trash |
| Backspace | Archive |
| Ctrl+Q / Ctrl+U | Mark read / unread |
| Ctrl+Shift+P | Pin / unpin |
| Insert | Favourite / unfavourite |
| Ctrl+Enter | Send (compose window; can ask first — Settings › Mail) |
| Ctrl+S | Save draft (compose window) |
| Ctrl+K | Insert link (compose window) |
| Esc | Close the compose window or dialog; clear a multiple selection |
| Ctrl/Shift+click | Select several messages (bulk archive, delete, read, move, favourite, junk; or drag onto a folder) |

The same list is in *Settings › Shortcuts*.

## Search

Type words to search subjects, people, attachment names and the text of messages stored on this PC (an SQLite FTS5
index; "invo" finds "invoice"). Operators narrow it down, and the form behind the search box writes them for you:

| Operator | Example |
| --- | --- |
| from:, to:, subject:, body: | `from:jane subject:"next year"` |
| account:, in:, folder: | `account:work in:sent`, `folder:projects` |
| is: | `is:unread`, `is:starred`, `is:pinned`, `is:followup`, `is:snoozed` |
| has:, filename:, filetype: | `has:attachment`, `filename:invoice`, `filetype:pdf` (or image, document, spreadsheet…) |
| larger:, smaller: | `larger:5mb` |
| after:, before:, on:, newer_than:, older_than: | `after:2026-01-31`, `newer_than:7d` |
| category: | `category:bills` |

Search covers mail stored on this PC; *Search on server* also asks your mail server about older mail. Smart folders
are saved searches.

## Demonstration data

Every person, company and address in the demo is fictional and uses reserved `.example` domains, so
nothing can ever reach a real mailbox. Accounts are labelled “(demo)”, the title bar shows **DEMO DATA**,
and sending only places the message in Sent. *Settings → Privacy → Clear local data* resets the sample mailbox.

## Licence

MIT — see [LICENSE](LICENSE).
