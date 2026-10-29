# SidePhase Mail: release readiness

**Version:** 0.7.0-beta.1 (Milestone 7) · **Assessed:** October 2026 · **Verdict: ready for a *controlled* beta with
IMAP/SMTP (password) accounts. Not production-ready.**

This page states what is actually verified, not what code exists for. A feature is only listed as tested when there
is evidence: automated tests (unit tests, or integration tests against a real, disposable Dovecot/Postfix server in
CI), the CI packaging test (MSIX install → launch → uninstall on Windows), or a check by the project owner on Windows.

## Before a public beta or Store release, these must be done

| Blocker | Why | Who |
| --- | --- | --- |
| **"Sent but not arriving / not in Sent" report** (see Known issues) | Reported on a real account during this milestone; not reproduced against the test server. Must be diagnosed with the new send log before any wider beta. | Owner + dev |
| Hand-check of the UI on Windows 11 | Most UI is verified only by compiling and starting (CI smoke test); see *Implemented but not fully tested*. | Owner / beta testers |
| Google OAuth app + verification | Real Google sign-in, IMAP/SMTP connection tests and Gmail inbox sync verified on 29 October 2026; public use needs restricted-scope verification and a security-assessment applicability determination ([ACCOUNT_PROVIDERS.md](docs/ACCOUNT_PROVIDERS.md#before-public-release-provider-configuration-and-verification)). | SidePhase Studio |
| Microsoft app registration + publisher verification | Outlook/Microsoft 365 sign-in never tested against Microsoft. | SidePhase Studio |
| Partner Center name reservation | Real package identity (Name/Publisher) replaces the placeholders in `Package.appxmanifest`. | SidePhase Studio |
| Code signing for non-Store packages | CI/beta MSIX are unsigned (testers use a local test certificate). | SidePhase Studio |
| Final artwork | Package tiles/logos are placeholders generated from the app logo. | SidePhase Studio |
| Privacy policy published on SidePhase Studio's domain | Required by Google, Microsoft and the Store. Draft: [PRIVACY.md](PRIVACY.md). | SidePhase Studio |

## Feature status

Legend: **Tested** = implemented and covered by automated tests (🔌 = also end to end against a real IMAP/SMTP server)
and/or verified on Windows by the owner · **Not fully tested** = implemented, compiles, starts, but not checked by hand
on Windows or against the real provider · **Partial** · **Not implemented**.

### Mail engine

| Feature | Status | Evidence / notes |
| --- | --- | --- |
| IMAP connect (TLS / STARTTLS), folder discovery, incremental sync (UID/UIDVALIDITY) | **Tested 🔌** | Integration suite runs twice per CI build (implicit TLS and STARTTLS). Owner added a real IMAP account (M3). |
| UIDVALIDITY safety | **Tested 🔌** | M6: operations on a renumbered folder are refused (no stale-UID deletes). |
| SMTP sending, Sent folder copy, no duplicate Sent copies | **Tested 🔌** / ⚠ see Known issues | End-to-end test: outbox → SMTP → recipient's inbox → exactly one Sent copy with a stable Message-ID. Owner sent to Gmail in M3. A new report says messages show as sent but don't arrive. |
| Crash safety of sending (no duplicates) | **Tested** | Single instance; outbox compare-and-set states; interrupted sends become *Uncertain* and are never resent automatically; stable Message-ID per outbox item; crash-left Sent copies filed once on next start. |
| Drafts (server + local copy) | **Tested 🔌** | M6: never replaced by a copy that lost attachments; retried saves don't duplicate. |
| Multiple accounts | **Tested 🔌** | Per-account isolation and sync; two-account tests. |
| Unified Inbox | **Not fully tested** | Logic tested; real multi-account Unified Inbox not hand-checked on Windows. |
| Message threading (conversation view) | **Tested** (logic) / **Not fully tested** (UI) | Message-ID/In-Reply-To/References grouping, cross-folder; 20,000-message grouping test. |
| Pinned messages, Favourites | **Tested 🔌** | Persist across restarts, sync, moves and UIDVALIDITY resets. |
| SpamAssassin detection | **Tested** | Header parsing, trust checks (forged sender-written headers ignored), thresholds. |
| Local spam learning (optional) | **Tested** (logic) / **Not fully tested** (UI) | Never moves or deletes mail. |
| Mail rules | **Tested 🔌** (engine) / **Not fully tested** (editor UI) | M6: full addresses must match exactly; never act on Junk. |
| Smart folders | **Tested** (logic) / **Not fully tested** (UI) | Never move mail. |
| Search (local FTS5 + server search) | **Tested 🔌** | Local index covers mail stored on this PC; UI says so. 20,000-message search in ~55 ms (CI-class machine). |
| Snooze, follow-up reminders | **Tested** (logic) / **Not fully tested** (UI) | Local to this PC; need the app running. |
| Scheduled send, Undo Send | **Tested 🔌** / **Not fully tested** (UI) | App must be running at the scheduled time. |
| Contacts (local, vCard import, autocomplete) | **Tested** (logic) / **Not fully tested** (UI) | Never uploaded. No sync with Google/Microsoft/CardDAV. |
| Attachments (streamed download, save, Mark of the Web) | **Tested 🔌** / **Not fully tested** (UI) | 3 MB streaming test; never opened or run by the app; MOTW added M6 (not hand-checked). |
| SQLite persistence and migrations | **Tested** | Transactional migrations (schema 1→2→3 tested), backup before upgrade, newer/damaged databases refused without changes, concurrent first-open safe. |
| Settings persistence | **Tested** | Durable writes, last-good backup, damaged/unreadable files never overwritten. |

### Accounts and sign-in

| Feature | Status | Evidence / notes |
| --- | --- | --- |
| Other (IMAP/SMTP with password or app password) | **Tested 🔌** | Known-provider table, autoconfig, ISPDB, guesses, manual settings, test before save. |
| Gmail OAuth | **Not fully tested** | Unit-tested with fake endpoints only; **never tested with Google**. Needs a client ID and verification. App passwords work meanwhile. |
| Microsoft OAuth | **Not fully tested** | Unit-tested with fake endpoints only; **never tested with Microsoft**. Outlook.com can't use passwords, so it needs a registered app. |
| OAuth refresh and failure handling | **Tested** (fakes) | M6: outages are temporary (no sign-in prompt); revoked tokens stop retrying; SMTP refreshes once. |
| Google/Microsoft choice for company domains | **Tested** (logic) | M6. |
| Thunderbird settings import (beta) | **Partial** | Server settings only (prefs.js), with consent; never passwords. Mail archives (mbox), address books (use Thunderbird's vCard export → Contacts → Import) and flags are **not imported**. |

### Windows, packaging and release

| Feature | Status | Evidence / notes |
| --- | --- | --- |
| MSIX package (identity, Start menu, icon, mailto: registration, notifications) | **Tested** (CI install/launch/uninstall) | Identity and artwork are placeholders. Default-mail-app selection and toast activation from the package not hand-checked. |
| Portable build | **Tested** (CI smoke test, owner use) | |
| Data copy from portable to installed app | **Tested** (unit + CI on Windows) | One-time copy; original untouched. |
| Clean uninstall | **Tested** (CI) | Removes the app and its LocalState (Windows behaviour). |
| Updates | **Partial** | Store-managed for Store builds; App Installer file generated only for signed beta packages; no in-app updater (by design). |
| Beta release workflow | **Implemented, not yet run** | Manual; creates a draft pre-release only after all tests pass. |
| Light/dark, Mica/Acrylic with solid fallbacks, high contrast | **Tested** (owner, M1) / **Not fully tested** since | Contrast themes map to system colours. |
| High DPI, multiple monitors, resizing | **Not fully tested** | Per-monitor-v2 DPI awareness; minimum size; centred on the current display. |
| Keyboard access, screen readers | **Partial** | Shortcuts and automation names on key controls; M7: message rows have spoken names, compose status is announced, visible focus in compose. No full Narrator pass yet. |
| Reduced motion | **Partial** | Standard XAML transitions and (M7) the reading-pane fade follow the Windows animation setting; two small custom press animations in `Styles.xaml` don't. |
| Footer promotion banner | **Tested** (logic) / **Not fully tested** (UI) | M7: one labelled banner in the footer; approved SidePhase Studio promotions bundled with the app only (none approved yet, so nothing shows); collapses when empty or in short windows. The remote SidePhase Ads API is used only when explicitly configured. |
| Offline (queued sends, offline drafts, status) | **Tested 🔌** (engine) / **Not fully tested** (real network loss on Windows) | M7: unreachable server = still queued, never failed; sent once on reconnect; drafts saved offline uploaded once; offline bar. |
| Message actions report real results | **Tested** (logic) / **Not fully tested** (UI) | M7: no "Done" when the server refused; partial results say so; Undo only for what moved. |
| Account storage (quota) | **Not fully tested** | M7: shown in Settings where the server supports IMAP QUOTA. |

## Known issues

1. **Messages reported as sent but not arriving and not in Sent (open, priority).** Seen on the owner's account
   during this milestone; not reproduced against the test server, where the same path (outbox → SMTP → recipient →
   Sent) passes. Two possible causes found during the audit are fixed in this milestone: the outbox could fail to
   start (then queued mail was never sent and the notice said "Sending…"), and a missing Sent folder was skipped
   silently. Next step: reproduce with this build and read the send log in `Logs\app.log`. It records each outbox
   state, the SMTP server's reply (e.g. `250 OK queued as …`) and where the Sent copy went, without addresses or
   content. See *Diagnosing a send problem* in [docs/BETA_TESTING.md](docs/BETA_TESTING.md).
2. First sync downloads the newest 500 messages per folder; older mail is reached with *Search on server*.
3. No IMAP IDLE (push): new mail arrives on the sync interval.
4. Drafts written in another app open without their attachments (their server copy is now kept when re-saved).
5. Scheduled send, snooze, reminders and rules work only while SidePhase Mail is running.
6. Removing an account keeps its outbox items (so nothing queued is lost); they show as failed.
7. Remote images (when allowed per message) can't be protected against public names that resolve to private addresses
   (WebView2's own protections apply).
8. The installed and portable builds keep separate data after the first copy.
9. No automated UI tests.
10. Message bodies and attachments are available offline only if they were opened (downloaded) while online.
11. Images pasted into a message are sent as attachments, not inline. Pasted images and forwarded attachments are kept
    in the data folder (`Pasted`, `Forwarding`) because drafts refer to them; they aren't cleaned up automatically yet.
12. Signatures are plain text.
13. A flag change made while a sync is running can occasionally be reverted by that sync until the next one.

## Performance (measured)

20,000 messages across two accounts with 10-message conversations (`LargeMailboxTests`, Linux CI-class machine):
loading the cache ~0.5 s (off the UI thread), full-text search ~55 ms, conversation grouping ~0.2 s, smart-folder
filtering < 1 ms. No optimisation was needed. Large attachments stream in 1 MB pieces and never load fully into memory.
Not yet measured: UI scrolling on large lists (virtualised ListView), sync of very large server folders beyond the
500-message first sync, offline start-up and network interruption on a real PC (the engine retries and reports
connection errors; tested with the test server only).

## Security and privacy

See [docs/SECURITY_REVIEW.md](docs/SECURITY_REVIEW.md) (findings and fixes in this milestone) and the
[privacy policy draft](PRIVACY.md).

### Google OAuth distribution follow-up

The Studio Desktop Client ID is now embedded in both build channels; Google client setup fields/secrets have been
removed from customer settings. Browser + PKCE, refresh, DPAPI storage and MailKit XOAUTH2 are reused.
See GOOGLE_SETUP.md for restricted-scope justification, assessment applicability and the release checklist.
Real Gmail consent, token exchange, the existing IMAP/SMTP connection checks, account creation and inbox sync were
verified on 29 October 2026 in the portable Windows build. The actual Gmail Inbox, Drafts, Sent Mail and labels appeared
and messages downloaded. Initial omission of the Desktop value produced `invalid_request`; supplying Google's issued
Desktop value fixed the exchange. 473 tests passed, 15 local-server integration tests skipped; portable publish and MSIX
build passed. No real send/delivery, expiry-driven refresh, revoke/reconnect, account-removal or installed-MSIX Google
sign-in test is claimed. Those remain in GOOGLE_SETUP.md's release checklist.
The temporary Google consent-screen Gmail support address must become support@sidephasestudio.com before release.
