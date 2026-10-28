# Security review (Milestone 6)

A review of the areas below was done for the beta, reading the code and tracing realistic attacks or failures.
Findings were fixed in this milestone unless marked *open*. Each fix has a regression test unless it is WinUI-only.

## Credential storage and OAuth tokens

* **Design:** passwords and OAuth tokens are stored outside SQLite, one file each in `Secrets\`, encrypted with
  Windows DPAPI (CurrentUser scope, app-specific entropy). They're readable only by the same Windows user on the same
  PC. Files are written via a temp file + rename. No secrets are in the repository, in CI or in settings files.
* **Fixed:**
  * Secret writes are now flushed to disk before the rename (a power cut can't leave an empty secret).
  * Reads no longer block a concurrent replace.
  * An unreadable secrets file is reported as such, rather than as "sign in again".
  * A damaged token record is treated as signed out.
* **Fixed (OAuth):**
  * Provider outages, captive portals and proxy error pages no longer count as "sign-in revoked".
  * A revoked refresh token stops repeated refresh attempts.
  * New tokens are saved before they're used.
  * SMTP refreshes a rejected token once.
  * Re-authenticating as a *different* Google/Microsoft account is refused, so a mailbox can't be silently swapped.
  * The browser page no longer claims success before the code exchange.
* **Verified:**
  * PKCE (S256) and a constant-time state check.
  * Loopback redirect on a random port.
  * Only the system browser is used.
  * No tokens in logs.
* *Open:* OAuth has never run against the real Google/Microsoft endpoints (see RELEASE_READINESS.md).

## HTML email rendering and WebView2

* **Verified:**
  * Script, web messages, host objects, dev tools, default dialogs, autofill and browser accelerator keys are disabled.
  * Permissions are denied and downloads cancelled.
  * The context menu is reduced to copy and select.
  * The document is served from a private virtual origin with CSP `default-src 'none'` (plus `form-action`/`base-uri 'none'`), `Referrer-Policy: no-referrer`, `nosniff` and `no-store`.
  * Every sub-resource request is intercepted and blocked unless remote images are allowed for that message.
  * Navigation inside the view is refused, and new windows go through the link confirmation.
* **Fixed:**
  * The sanitiser now repeats until the output is stable, so removed tags can't reassemble (`<lin<link>k rel=preconnect …>` → working `<link>`). Content that never stabilises is shown as text.
  * Event-handler attributes after `/` are removed.
  * `X-DNS-Prefetch-Control: off` is set, and WebView2 runs with link preconnect/prefetch features disabled, so opening a message can't signal "read" through DNS or socket preconnects even with remote content blocked.
* **Fixed (remote images):** when the user allows them for a message, images load only from public addresses. Requests to localhost, private, link-local (including cloud metadata) and local-network names are refused, and cookies are stripped.
* *Open:* a public hostname that resolves to a private address isn't caught by the URL check (WebView2's own private-network protections still apply).

## External links

* **Verified:**
  * Only `http(s)` and `mailto:` are acted on; `file:`, `javascript:` and `ms-*` are ignored.
  * Web links open in the default browser only after a confirmation that shows the real host and URL, with a warning for plain `http`.
  * `mailto:` (from messages and, for the installed app, from Windows) opens a new message with recipients, subject and body only; `attach=` and similar parameters are ignored, and nothing is sent automatically.

## Attachments and file downloads

* **Verified:**
  * Attachments are never opened or run by the app.
  * Preview is limited to a few image types and plain text, with size caps.
  * Path separators, alternate data streams (`:`) and control characters are removed from names, which are capped at 180 characters.
  * *Save all* never overwrites (unique names).
* **Fixed:**
  * The "could harm your PC" check now judges the name the file is actually saved under. Trailing Unicode whitespace, dots and direction controls previously hid `.exe`.
  * The list of risky types now includes legacy Office macro formats, OneNote, `.rdp`, `.iqy`, `.slk`, `.search-ms`, `.appref-ms`, `.diagcab`, `.jnlp` and local HTML/SVG pages.
  * All Unicode direction controls are neutralised, and the warning shows the saved name.
  * Reserved device names are caught whatever follows the first dot.
* **Fixed:** saved attachments get **Mark of the Web** (zone 3), so SmartScreen, Office Protected View and macro blocking apply.
* **Fixed:** downloads go to a temporary file and are moved into place, so a failed download never destroys an existing file.

## SQLite storage

* **Verified:** all SQL is parameterised (including FTS `MATCH`). The cache sits in the user's local app data (NTFS permissions).
* **Fixed:**
  * Migrations can't run twice concurrently.
  * A backup is taken before each schema upgrade.
  * Damaged or newer databases are never changed without the user's agreement.
  * Removing an account also removes its rule history and spam-training records.
* *Accepted:*
  * Mail bodies and the search index are stored unencrypted, as in other desktop mail clients; Windows account security / BitLocker protects them.
  * Deleted rows may remain in free pages until SQLite reuses them (`secure_delete` is off for performance).

## Diagnostic logging

* **Verified:** logs pass through `LogRedactor` (bearer/XOAUTH2/SASL-style credentials, OAuth tokens and codes, passwords, JWTs, user part of email addresses). They rotate at 1 MB and stay local. No IMAP/SMTP protocol logging is enabled.
* **Fixed:**
  * Inner exceptions (which can carry server replies) are logged only by type and redacted, truncated message.
  * Logs include app version, channel and OS to make reports useful.
  * The new send trail records outbox states, the SMTP server's reply and the Sent folder used, with no addresses, subjects or content.

## Email filtering rules

* **Verified:**
  * Rules can't delete, forward or reply.
  * Spam-score conditions trust only server-added headers.
  * Moves stay within the account.
  * Each rule acts on a message once.
* **Fixed:**
  * A sender condition with a full address now needs an exact match. Previously any address *containing* it, or a display name set by the sender, matched.
  * `@domain` means that domain and its subdomains.
  * Rules no longer act on Junk or locally junked mail, which previously let spoofed mail be lifted out of Junk.
* *Open (low):* the once-per-message record uses message identity, which includes the sender-controlled Message-ID. A sender could pre-empt a rule for a later message with the same Message-ID. Impact: the rule doesn't run on that message.

## Instance and update security

* **Fixed:** single instance per data folder, so two processes can't send the same scheduled message or sync over each other.
* **Verified:**
  * There is no in-app updater, and the app never downloads or runs executables.
  * Updates come only from the Microsoft Store or Windows App Installer, which verify package signatures.
  * CI never holds a signing key: the install test uses a throwaway certificate created and deleted on the runner, and uploaded packages are unsigned.

## Sponsored content

* The module can't see mail data: it references no mail assemblies (tested).
* Its only request type carries placement, app version and language (tested).
* Cards are plain text with HTTPS links and pass the link confirmation.
* Production advertising is disabled in code.
