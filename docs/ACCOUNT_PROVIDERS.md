# Accounts, providers, OAuth and security

This document covers how SidePhase Mail connects to mail providers, how to register the OAuth apps that
Google and Microsoft sign-in need, where data is stored, and the security decisions behind it.

> **Verification status.** IMAP/SMTP with passwords is covered by automated tests against a real
> (disposable) Dovecot/Postfix server in CI. **Google and Microsoft OAuth sign-in have not yet been tested
> against the real providers**: the code (loopback redirect, PKCE, token exchange and refresh, XOAUTH2) is unit-tested
> with fake token endpoints only. It needs to be checked with real app registrations before it is described as working.

## How account setup works

1. **Find settings** for the address (`AccountDiscovery`):
   1. A built-in table of well-known providers (Gmail, Outlook.com/Hotmail/Live, Yahoo, iCloud, AOL, Fastmail, Zoho, GMX).
   2. Mozilla-style autoconfig over **HTTPS only**: `https://autoconfig.<domain>/mail/config-v1.1.xml`,
      then `https://<domain>/.well-known/autoconfig/mail/config-v1.1.xml` (these go to the user's own provider),
      then the Thunderbird ISP database `https://autoconfig.thunderbird.net/v1.1/<domain>` (it receives the domain only, never the address).
   3. Otherwise a **guess** (`imap.<domain>:993`, `smtp.<domain>:465`, TLS), clearly labelled as unconfirmed.
   Unencrypted (`plain`) servers in autoconfig documents are discarded.
2. **Sign in**: Google and Microsoft accounts use OAuth in the system browser; everything else uses a password
   (often an app password). Server settings can always be edited by hand.
3. **Test** IMAP and SMTP logins. **Nothing is saved unless both succeed.**
4. **Save** the account (SQLite, no secrets), then save the password or tokens to the encrypted secret store, then start syncing.

Microsoft 365 work/school accounts on custom domains: use *“Using Microsoft 365 for work or school?”* on the
Add Account page (IMAP `outlook.office365.com:993`, SMTP `smtp.office365.com:587` STARTTLS, OAuth). The tenant
admin must allow IMAP/SMTP AUTH for the mailbox.

| Provider | IMAP | SMTP | Sign-in |
| --- | --- | --- | --- |
| Gmail | imap.gmail.com:993 TLS | smtp.gmail.com:465 TLS | Google OAuth (or an app password with 2-Step Verification) |
| Outlook.com / Hotmail / Live / MSN | outlook.office365.com:993 TLS | smtp-mail.outlook.com:587 STARTTLS | Microsoft OAuth (passwords are no longer accepted by Microsoft) |
| Microsoft 365 | outlook.office365.com:993 TLS | smtp.office365.com:587 STARTTLS | Microsoft OAuth |
| Yahoo / AOL | imap.mail.yahoo.com / imap.aol.com :993 | :465 TLS | App password |
| iCloud | imap.mail.me.com:993 TLS | smtp.mail.me.com:587 STARTTLS | App-specific password |
| Fastmail, Zoho, GMX | see `AccountDiscovery.cs` | | App password / password |

Proton Mail needs Proton Mail Bridge, whose self-signed certificate is rejected by SidePhase Mail's strict
certificate validation. It isn't supported yet.

## Google OAuth (Gmail)

SidePhase Mail uses Google's *installed application* flow: the system browser opens Google's own sign-in page,
Google redirects to a temporary loopback address (`http://127.0.0.1:<random port>/`), and the app exchanges the
code using **PKCE (S256)** and a random `state` that must match. SidePhase Mail never sees the Google password.

Scopes requested: `https://mail.google.com/` (IMAP/SMTP access; required by Google for XOAUTH2), `openid`, `email`
(to confirm which address signed in). `access_type=offline` is requested so a refresh token is issued.

### Registering the app (Google Cloud Console)

1. Create a project at <https://console.cloud.google.com/>.
2. **APIs & Services → OAuth consent screen**: user type *External*; app name *SidePhase Mail*; add the scopes
   above. While the app is in *Testing*, add the Google accounts that may sign in as **test users**.
3. **Credentials → Create credentials → OAuth client ID → Desktop app.** Loopback redirects are allowed
   automatically for desktop clients.
4. Give the app the client ID: *Settings → Accounts → Google OAuth client ID*, or the environment variable
   `SIDEPHASE_GOOGLE_CLIENT_ID`. Google also issues a “client secret” for desktop clients; for installed apps
   it is not confidential, but it's still never compiled in or committed. Enter it under
   *Settings → Accounts → Google client secret* (stored encrypted) or set `SIDEPHASE_GOOGLE_CLIENT_SECRET`.

### Restricted scope verification

`https://mail.google.com/` is a **restricted scope**. Until Google verifies the app, only the test users
(max. 100) can sign in, and refresh tokens for apps in *Testing* expire after 7 days. Publishing to everyone requires
Google's OAuth verification and an annual third-party security assessment (CASA). Until then, Gmail users can also
connect with an **app password** (requires 2-Step Verification), which the Add Account page offers.

## Microsoft OAuth (Outlook.com, Hotmail, Live, Microsoft 365)

Same loopback + PKCE flow against the Microsoft identity platform (`https://login.microsoftonline.com/common/oauth2/v2.0`),
as a **public client** (no secret).

Scopes: `https://outlook.office.com/IMAP.AccessAsUser.All`, `https://outlook.office.com/SMTP.Send`,
`offline_access`, `openid`, `email`. No Microsoft Graph permissions are requested.

### Registering the app (Microsoft Entra)

1. <https://entra.microsoft.com/> → **App registrations → New registration**.
2. Supported account types: *Accounts in any organizational directory and personal Microsoft accounts*.
3. Redirect URI: platform **Mobile and desktop applications**, URI `http://localhost`. (Loopback ports are
   matched loosely for this platform, so the random port works.)
4. **Authentication → Allow public client flows: Yes.**
5. **API permissions → Add → APIs my organization uses → Office 365 Exchange Online** (delegated)
   `IMAP.AccessAsUser.All` and `SMTP.Send`, plus `offline_access`, `openid`, `email`.
6. Copy the *Application (client) ID* into *Settings → Accounts → Microsoft application ID*, or set
   `SIDEPHASE_MICROSOFT_CLIENT_ID` (optionally `SIDEPHASE_MICROSOFT_TENANT`, default `common`).

Work/school tenants may require admin consent, and the mailbox must have IMAP and SMTP AUTH enabled.

### Why IMAP/SMTP rather than Microsoft Graph, and why not MSAL (yet)

* **Graph vs IMAP**: using IMAP/SMTP for every provider keeps one sync engine, one cache model and the same
  folder/flag semantics for Gmail, Microsoft and everyone else. Graph would add a second engine and broader
  permissions (`Mail.ReadWrite`). It could be added later behind `IMailDataSource` if IMAP limits become a problem.
* **MSAL**: MSAL.NET would add the Windows broker (WAM) and its own token cache. SidePhase Mail currently uses
  one small RFC 8252 client for both providers so all tokens live in the same encrypted store and are refreshed
  the same way. Switching Microsoft sign-in to MSAL + WAM is a sensible follow-up once real sign-in is being tested.

## Where data is stored

Everything is under `%LOCALAPPDATA%\SidePhase Mail\`:

| Path | Contents |
| --- | --- |
| `mail-cache.db` | SQLite cache: accounts (name, address, server names/ports, **no secrets**), folders, sync state (UIDVALIDITY, highest UID), message headers, previews, downloaded bodies, pins, local favourites, drafts. |
| `Secrets\*.secret` | Passwords and OAuth tokens, each encrypted with **Windows DPAPI** (current user) plus app-specific entropy. File names are hashes. |
| `settings.json` | Preferences, favourite folders, signatures, OAuth client IDs (public identifiers). |
| `Logs\` | Start-up and error logs. Never message contents, passwords or tokens. |
| `WebView2\` | The message renderer's browser profile (no credentials). |

*Settings → Privacy → Clear local data* removes cached messages (they download again). *Remove account*
deletes that account's cache, pins and saved credentials. **Neither ever deletes mail on the server.**

## Security decisions

* **No plaintext passwords or tokens on disk.** Secrets go only to the DPAPI-protected secret store; SQLite has
  no password or token columns (a test checks this). A damaged or foreign secret is treated as missing, so the user is asked to sign in again.
* **No secrets in logs or error messages.** Errors are translated into friendly text. Sign-in failures never include the server's reply; other refusals (e.g. a rejected recipient) may quote it, but passwords and tokens are never part of an error message.
* **Nothing is compiled in.** OAuth client IDs and the Google client secret come from settings, the encrypted store or environment variables.
* **Encrypted connections only.** SSL/TLS from the start or STARTTLS that must succeed; there is no plain-text
  option, and a failed STARTTLS is an error, never a downgrade. Certificates are validated by Windows; certificate errors are shown, never bypassed.
* **Minimal OAuth permissions**: mail access for IMAP/SMTP and the user's address only.
* **All traffic is between the PC and the user's provider.** Nothing — mail, addresses, credentials or analytics — is sent to SidePhase Studio.
  The only other network request is the optional autoconfig lookup described above.
* **No server changes without an explicit action.** Sync is read-only (bodies are fetched with `BODY.PEEK`).
  The server is only changed when the user marks read/unread (including opening a message, if *Mark as read on open* is on), stars,
  moves, archives, deletes, saves a draft or sends. Permanent deletion only happens from Trash after confirmation (and saving a draft replaces that draft's previous copy). Expunging uses
  `UID EXPUNGE` (UIDPLUS), so messages other apps flagged for deletion are never removed; without UIDPLUS a message is only flagged `\Deleted`.
* **Duplicate sends are prevented** (an identical draft can't be sent twice at once), and *Sent* is only reported once the SMTP server has accepted the message.
* **Message rendering**: sanitised HTML in a locked-down WebView2 (no scripts, strict CSP, no host objects), remote images blocked by default,
  inline `cid:` images embedded as `data:` URIs, links opened only after a confirmation showing their destination, attachments saved through a file picker and never opened or executed.
