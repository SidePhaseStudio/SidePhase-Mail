# Development progress

Legend: ✅ implemented · 🧪 implemented and covered by automated tests · 🔌 tested end-to-end against a real IMAP/SMTP server (CI) · ❔ implemented, not yet verified · 🟡 partial · ⏳ planned

Automated tests: `tests/SidePhase.Mail.Tests` (~185 xUnit tests). Unit tests run on Windows and Linux; the
integration tests run in CI against a throwaway Dovecot/Postfix server (`scripts/ci/setup-test-mail-server.sh`), never real accounts.
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

## Milestone 2 — Real email accounts

| Feature | Status | Notes |
| --- | --- | --- |
| IMAP via MailKit: TLS/STARTTLS, login, folder discovery (special-use + names) | 🔌 | `MailConnector`, `ImapSynchronizer`, `FolderClassifier`. |
| Incremental sync with UID/UIDVALIDITY (new mail, flag changes, removals, resets) | 🔌 | `SyncPlanner` (unit-tested). First sync downloads the newest 500 messages per folder. |
| Bodies on demand (BODY.PEEK), cached; inline `cid:` images | 🔌 | |
| Read/unread, star (`\Flagged`), move, archive, trash, permanent delete, undo | 🔌 | Optimistic UI, reverted with an error toast if the server refuses. UIDPLUS-safe expunge. |
| SMTP sending: new, reply (threaded), reply all, forward, CC/BCC, attachments, HTML + text | 🔌 | Success only after the server accepts; duplicate-send guard; copy appended to Sent (not for Gmail/Microsoft, which save it themselves). |
| Drafts saved to the server's Drafts folder | 🔌 | Local copy kept first so nothing is lost offline. |
| Multiple accounts, background sync, per-account error isolation | 🔌 | Sync interval from Settings. No IMAP IDLE yet. |
| SQLite cache: migrations, stable ids, indexes, no duplicates | 🧪 | `MailDatabase`, `MailCache`. |
| Persistent pins (survive restart, sync, moves, UIDVALIDITY resets) | 🔌 | Keyed by account + Message-ID, independent of IMAP stars. |
| Favourites: starred messages synced as `\Flagged`, local fallback; favourite folders | 🔌 / ❔ | Favourite folders (sidebar section, right-click a folder) not yet verified on Windows. |
| Real unified inbox with account colour markers | ❔ | Same list design; needs checking on Windows. |
| Encrypted credential storage (DPAPI), account removal | 🧪 | DPAPI round-trip tested on the Windows CI runner. |
| Account setup: known providers, HTTPS autoconfig, ISPDB, guesses, manual settings, test before save | 🧪 / 🔌 | Add Account page ❔ (needs checking on Windows). |
| Google OAuth 2.0 (loopback + PKCE) | ❔ | Unit-tested with fake endpoints only. **Not tested with a real Google account yet** — needs a client ID (see docs/ACCOUNT_PROVIDERS.md). |
| Microsoft OAuth (Outlook.com/Hotmail/Live/M365) | ❔ | Same: **not tested with a real Microsoft account yet**. |
| Token refresh, expired sign-in handling | 🧪 | Refresh and `invalid_grant` tested with fakes; "Sign in again" in Settings. |
| Safe links (confirmation shows destination), attachment saving (never executed) | ❔ | Needs checking on Windows. |
| Friendly errors (auth, TLS, network, SMTP rejection, timeouts) | 🧪 | `MailErrorTranslator`; deduplicated toasts. |
| Demo mode kept and never mixed with real accounts | 🧪 | `MailDataSourceRouter`; toggle in Settings → Accounts. |

## Milestone 3 — Productivity, spam protection and message management

Verified by the user on Windows 11: adding a real IMAP account (not Gmail/Microsoft), sending to Gmail.

| Feature | Status | Notes |
| --- | --- | --- |
| Sent folder: no duplicate copies | 🔌 | The app's own Sent copy is skipped when the server already filed one (Message-ID check). Exactly one copy after send + sync is tested. |
| Quick actions (archive, delete → Trash, read/unread, pin, favourite) with undo | 🔌 / ❔ | Server effects tested; hover toolbar now overlays the date and only fades, so hovering can't move the list (❔ needs checking on Windows). Shortcuts: Backspace, Delete, Ctrl+Q, Ctrl+U, Ctrl+Shift+P, Insert. |
| Junk keywords for server spam filters | 🔌 | Moving to/from Junk sets `$Junk` / `$NotJunk` where the server allows keywords. |
| Pinned emails | 🔌 | Persistent in SQLite by message identity; survive restart, sync, moves and UIDVALIDITY resets; separate from stars; collapsible section. |
| Favourites | 🔌 | Stars sync as IMAP `\Flagged` (local fallback), combined Favourites view, favourite folders. |
| SpamAssassin detection | 🧪 | X-Spam-Status/Flag/Score/Level/Report parsing, per-rule breakdown, configurable threshold, conflicting-header detection. Spam headers are trusted only when a server added them in transit (checked against Received headers); headers the sender wrote are ignored; missing headers are never treated as safe. Details shown in the reading pane (❔ UI). |
| Send the Bugger Back! | 🧪 | Options: block sender, block domain (never shared providers), move to Junk, apply to future messages; undo (including restoring a removed trust). Silent: no replies, bounces or backscatter. |
| Spam settings section | ❔ | Threshold, trusted/blocked senders and domains, high-score filtering, optional server-side Junk rule for blocked senders (off by default), filtered-mail notices. |
| Automatic Junk rule | 🧪 | Only blocked senders/domains, only new Inbox mail, never trusted senders, never deletes, never acts on spam scores alone. |
| Search | 🧪 / 🔌 | from:, to:, subject:, body:, is:, has:attachment, after:, before:, on:, newer_than:, older_than:. Words also match downloaded message text (SQLite, off the UI thread). *Search on server* (IMAP SEARCH) finds older mail and downloads its headers; results are labelled as "this PC" or "server". |
| Windows desktop notifications | ❔ | Windows App SDK notifications with sender, subject and preview; click opens the message; per-account mute; never for Junk/blocked senders; each message at most once; respects Windows notification settings; falls back to in-app notices. Notification logic is unit-tested; the Windows toasts themselves need checking on a PC. |
| Responsiveness | ✅ | Cache reads run off the UI thread; bursts of sync events coalesce into one refresh; sent-mail follow-up (Sent copy, draft clean-up) runs after the send returns. |

## Future

Calendar, contacts, snooze, scheduled send, follow-up reminders, Discord and other services — the
`IMailDataSource` boundary, the scope model and the sidebar's data-driven navigation leave room for these. Not started.

## Known limitations

* **Google and Microsoft sign-in are unverified** until tested with real app registrations; Gmail works today with an app password.
* Parts of the Windows UI marked ❔ compile and pass the CI smoke test but haven't been checked by hand yet.
* Server search downloads at most 200 matches per folder.
* First sync downloads the newest 500 messages per folder; older mail isn't back-filled yet. Gmail's All Mail is used as the
  archive target but isn't downloaded (it repeats every message).
* No IMAP IDLE (push); new mail arrives on the sync interval. No CONDSTORE/QRESYNC optimisation yet.
* Drafts edited in another app open without their attachments.
* Proton Mail Bridge (self-signed certificate) isn't supported.
* No automated UI tests yet.
