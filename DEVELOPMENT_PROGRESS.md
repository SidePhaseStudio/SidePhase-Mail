# Development progress

Legend: ✅ implemented · 🧪 implemented and covered by automated tests · 🔌 tested end-to-end against a real IMAP/SMTP server (CI) · ❔ implemented, not yet verified · 🟡 partial · ⏳ planned

Automated tests: `tests/SidePhase.Mail.Tests` (~350 xUnit tests). Unit tests run on Windows and Linux; the
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
| Keyboard shortcuts | ✅ | See Milestone 4 for the current list. |
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
| Spam settings section | ❔ | Threshold, trusted/blocked senders and domains, high-score filtering, optional server-side Junk rule for blocked senders (off by default), filtered-mail notices. |
| Automatic Junk rule | 🧪 | Only blocked senders/domains, only new Inbox mail, never trusted senders, never deletes, never acts on spam scores alone. |
| Search | 🧪 / 🔌 | from:, to:, subject:, body:, is:, has:attachment, after:, before:, on:, newer_than:, older_than:. Words also match downloaded message text (SQLite, off the UI thread). *Search on server* (IMAP SEARCH) finds older mail and downloads its headers; results are labelled as "this PC" or "server". |
| Windows desktop notifications | ❔ | Windows App SDK notifications with sender, subject and preview; click opens the message; per-account mute; never for Junk/blocked senders; each message at most once; respects Windows notification settings; falls back to in-app notices. Notification logic is unit-tested; the Windows toasts themselves need checking on a PC. |
| Responsiveness | ✅ | Cache reads run off the UI thread; bursts of sync events coalesce into one refresh; sent-mail follow-up (Sent copy, draft clean-up) runs after the send returns. |

## Milestone 4 — Productivity

Timed features (snooze, reminders, scheduled send, Undo Send) use a controllable clock in tests and store every time in
UTC, so daylight-saving changes and time-zone moves don't shift them. A custom time zone with DST is used in the tests.
UI items below compile and pass the CI smoke test; ❔ means not yet checked by hand on Windows.

| Feature | Status | Notes |
| --- | --- | --- |
| Snooze (Later today, Tomorrow morning, This weekend, Next week, choose date & time) | 🧪 / ❔ | Right-click or reading pane ⋯. Stored in SQLite by message identity, so it survives restarts, sync and moves; hidden everywhere except **Snoozed**; returns automatically (checked every 30 s and at start-up); Unsnooze early; every account. **Local to this PC: other mail apps still show the message in its folder.** |
| Follow-up reminders (Tomorrow, In 3 days, Next week, custom) | 🧪 / ❔ | **Follow-ups** view, flag on the row (red when overdue), reading-pane banner with Mark done, Windows notification when due (in-app fallback), announced once per reminder. Never changes or sends the message. |
| Schedule send | 🧪 / 🔌 / ❔ | Dropdown beside Send. Outbox state machine: Queued → Sending → Accepted / Failed / Uncertain (+ Cancelled), compare-and-set transitions so a message is sent at most once; attempts recorded; attachments copied privately until sent. Temporary failures retry (1, 5, 15 min); rejections fail; a broken connection after the data was sent is **Uncertain and never resent automatically** — the Scheduled page asks you. Never sent early after a clock change; more than a day late is held for you to decide. **The app must be running (it can be minimised); nothing is scheduled on the server.** |
| Undo Send (off, 5, 10, 20, 30 s; default 10) | 🧪 / 🔌 / ❔ | "Sending… Undo" notice; Undo works only before the message reaches the SMTP server and reopens it for editing. A sent message can't be recalled. |
| Scheduled page | ❔ | Send now, reschedule, edit, cancel, retry, "It was sent" / "Send again…" for uncertain deliveries, recent history. Sidebar badge counts what's waiting or needs attention. |
| Conversation view (Settings › Mail, on by default) | 🧪 / ❔ | Gmail-style: one expandable row per conversation, linked by Message-ID/References/In-Reply-To (subject fallback only for header-less replies sharing a participant, within 60 days); includes your replies from Sent; actions apply to the whole conversation; never spans accounts. |
| Contacts | 🧪 / ❔ | SQLite address book (names, several addresses, notes), search, add/edit/delete, vCard 2.1/3.0/4.0 import with merging, recent recipients. Kept on this PC only, never uploaded. Each contact records its source so Google/Microsoft/CardDAV sync can be added later (`IContactSyncSource`). |
| Recipient autocomplete | 🧪 / ❔ | To/Cc/Bcc suggest contacts and people you've sent to, ranked by match, frequency and recency. |
| Attachments | 🧪 / 🔌 / ❔ | Streamed to disk with progress (1 MB IMAP pieces → temporary file → decoded), never fully in memory; Save all into a folder with unique names; warnings for files that can run code and disguised names; safe file names; in-app preview for images and plain text only. Never opened or run. |
| Compose | 🧪 / ❔ | Signature library with per-account defaults (old signatures migrated) and a per-message picker; crash-recovery copy every 5 s and Drafts autosave every 2 min; recovered messages offered at start-up; strikethrough, numbered lists, links (web/mailto only), clear formatting; standard HTML output; drag-and-drop attachments with duplicate-name numbering. |
| Accounts | 🧪 / 🔌 / ❔ | Rename and colour (only those columns change; settings and saved sign-ins untouched), default sending account, per-account check interval, connection status, sign in again, safe removal. |
| Shortcuts | ❔ | Ctrl+N, Ctrl+R, Ctrl+Shift+R, Ctrl+Shift+F (forward), Ctrl+F / Ctrl+E (search), F5, Delete → Trash, Backspace, Ctrl+Q/U, Ctrl+Shift+P, Insert; compose: Ctrl+Enter (optional confirmation), Ctrl+S, Ctrl+K, Esc. Reference in Settings › Shortcuts. |
| Database migration | 🧪 | Schema 2 only adds tables; accounts, messages and saved sign-ins are untouched (tested from a schema-1 database). |

## Milestone 5 — Smart inbox, search, rules and spam

Checked first: Milestones 3 and 4 build and pass CI; conversation view, search and junk handling already existed and
were extended rather than duplicated. UI items compile and pass the CI smoke test; ❔ means not yet checked by hand.

| Feature | Status | Notes |
| --- | --- | --- |
| Conversation view | 🧪 / ❔ | (Milestone 4) Message-ID/In-Reply-To/References grouping, across folders incl. Sent, per account, expand/collapse, settings switch. New: the reading pane lists every message in the conversation (sender, preview, date, attachments, unread) and opens any of them to read or reply to that one. |
| Search index (SQLite FTS5) | 🧪 / 🔌 | Subjects, people, downloaded text and attachment names; maintained by triggers (no rebuilds), filled from existing mail by the schema-3 migration; prefix matching, diacritics folded, user input can't inject FTS syntax. Only mail stored on this PC is indexed, and the UI says so; *Search on server* covers the rest. |
| Advanced search | 🧪 / ❔ | Operators for account, folder, location, size, attachment name/type, category, follow-ups; a compact form behind the search box that reads and writes the same query; "this view" or "all mail". |
| Attachment details | 🔌 | Names, types and sizes from BODYSTRUCTURE during sync; older cached mail is filled in gradually; nothing is downloaded to learn them. |
| Smart folders | 🧪 / ❔ | Saved searches in SQLite (never move mail): Unread, With attachments, Recently received, Large messages to start; create/edit/delete; unread counts. |
| Spam | 🧪 / ❔ | SpamAssassin handling kept (verified headers only, explanations per rule, threshold, trusted/blocked senders and domains, Junk folder integration, Mark Junk / Not junk). New: score bar in the details. |
| Local spam learning (optional) | 🧪 / ❔ | Naive Bayes on sender domain, subject and preview, trained only by explicit Junk / Not junk on real accounts; local only; explains its opinion; reset in Settings. It never moves or deletes; listing very sure messages (97%+) under Junk is optional and off by default. |
| Rules | 🧪 / 🔌 / ❔ | Sender, domain, recipient, subject, keywords, verified spam score, attachments; all/any, per account, stop processing. Actions: move, mark read, favourite, category, no notification. Run on new Inbox mail before notifications, once per message per rule (by identity, so moves don't re-trigger), and "Run now" on a folder. Never deletes. |
| Categories | 🧪 / ❔ | Local labels by message identity; chip on the row; edit from the context menu; `category:` in search and smart folders. |
| Bulk actions | 🧪 / 🔌 / ❔ | Multi-select, bulk archive/delete/read/unread/move/favourite/junk with undo, drag to sidebar folders; moves are one UID MOVE per 200 messages with progress; cache work runs off the UI thread. |
| Attachment finder | 🧪 / ❔ | Sidebar › Attachments: by name, type, size, sender, date; opens the containing email; uses recorded details only; never opens files. |
| Privacy & security review | 🧪 | Diagnostic logs now redact tokens, bearer/XOAUTH2 headers, passwords, auth codes, JWTs and the user part of email addresses, and rotate at 1 MB. Tokens and passwords stay DPAPI-encrypted outside SQLite; HTML stays sanitised with a strict CSP; remote images blocked by default; links confirmed; risky attachments warned about and never run. No cloud scanning. |
| Performance | ✅ | List filtering, search, conversation grouping and sorting run off the UI thread; incremental FTS; batched IMAP moves; attachment details back-filled a few hundred per folder per sync. |
| Database migration | 🧪 | Schema 3 only adds tables, indexes and triggers; tested from a schema-2 database with cached mail (indexed on upgrade, accounts untouched). |

## Future

Calendar, CardDAV/Google/Microsoft contact sync, IMAP IDLE, server-side scheduled send where providers offer it.

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
* Snoozes, follow-up reminders and conversation grouping are local to this PC and don't sync to other mail apps.
* Scheduled messages and Undo Send need SidePhase Mail to be running; a message that couldn't be sent within a day of its
  time is held rather than sent late.
* A message reopened from Undo Send, the Scheduled page or crash recovery keeps its text but loses rich formatting.
* Search, smart folders and the attachment finder cover mail stored on this PC (the newest 500 per folder on first sync);
  older mail is found with *Search on server*.
* Rules run while SidePhase Mail is open, on new Inbox mail; they aren't server-side filters.
* Local categories and spam learning aren't shared with other mail apps.
