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

### Studio registration and public verification

The application-wide Studio Desktop Client ID is embedded at build time from `SidePhaseGoogleClientId`
in `Directory.Build.props`. Gmail customers only choose **Add account → Google → Sign in with Google**;
they never enter registration credentials. The issued public Desktop registration value is supplied through publisher build configuration. Developer overrides and exact Cloud Console
steps are in [GOOGLE_SETUP.md](../GOOGLE_SETUP.md), including test users, real-account tests and release verification.

Full-mail access is restricted. The existing confirmed permanent-delete feature is part of its scope justification;
Google must approve the justification. Public distribution requires brand and restricted-scope verification.
A third-party security assessment may apply if restricted data is accessed from or through third-party servers;
request a determination for this local-only architecture rather than assuming an assessment is always required.
Testing-mode tokens normally expire after seven days. See the setup guide for current official references.

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
6. Publisher: set `SidePhaseMicrosoftClientId` in `Directory.Build.props` to the public Application (client) ID.
   CI can override it with repository variable `SIDEPHASE_MICROSOFT_CLIENT_ID`. Customers do not enter IDs.
   Developers can override the bundled ID with environment variable `SIDEPHASE_MICROSOFT_CLIENT_ID`
   (tenant override `SIDEPHASE_MICROSOFT_TENANT`, default `common`). No Microsoft secret is needed.

Publisher registration and real Microsoft mailbox sign-in are pending. See [MICROSOFT_SETUP.md](../MICROSOFT_SETUP.md).

Work/school tenants may require admin consent, and the mailbox must have IMAP and SMTP AUTH enabled.

### Why IMAP/SMTP rather than Microsoft Graph, and why not MSAL (yet)

* **Graph vs IMAP**: using IMAP/SMTP for every provider keeps one sync engine, one cache model and the same
  folder/flag semantics for Gmail, Microsoft and everyone else. Graph would add a second engine and broader
  permissions (`Mail.ReadWrite`). It could be added later behind `IMailDataSource` if IMAP limits become a problem.
* **MSAL**: MSAL.NET would add the Windows broker (WAM) and its own token cache. SidePhase Mail currently uses
  one small RFC 8252 client for both providers so all tokens live in the same encrypted store and are refreshed
  the same way. Switching Microsoft sign-in to MSAL + WAM is a sensible follow-up once real sign-in is being tested.

## Where data is stored

The portable build uses `%LOCALAPPDATA%\SidePhase Mail\`; the installed (MSIX) app uses its package folder
`%LOCALAPPDATA%\Packages\<package family>\LocalState\` (and copies the portable data once on first start; see
[PACKAGING.md](PACKAGING.md)). Both contain:

| Path | Contents |
| --- | --- |
| `mail-cache.db` | SQLite cache: accounts (name, address, server names/ports, **no secrets**), folders, sync state (UIDVALIDITY, highest UID), message headers, previews, downloaded bodies, the search index, pins, local favourites, drafts, snoozes, reminders, the outbox, contacts, rules, smart folders, categories, local spam-learning word counts. |
| `Secrets\*.secret` | Passwords and OAuth tokens, each encrypted with **Windows DPAPI** (current user) plus app-specific entropy. File names are hashes. |
| `settings.json` (+ `.bak`) | Preferences, favourite folders, signatures, OAuth client IDs (public identifiers). `.bak` is the previous good copy. |
| `advertising.json` | Sponsored-content preferences (off by default). |
| `Recovery\` | Crash-recovery copies of messages being written. |
| `Outbox\` | Attachments of queued/scheduled messages, and Sent copies waiting to be filed (`SentCopies\`). |
| `Backups\` | Copies of the mail cache taken before schema upgrades (three kept), and a damaged cache if you chose to set it aside. |
| `Logs\` | Start-up, error and send-trail logs. Never message contents, subjects, passwords or tokens; email addresses are reduced to the domain. |
| `WebView2\` | The message renderer's browser profile (no credentials). |

*Settings → Privacy → Clear local data* removes cached messages (they download again). *Remove account*
deletes that account's cache, pins and saved credentials. **Neither ever deletes mail on the server.**

## Before public release: provider configuration and verification

**Google and Microsoft sign-in are not ready for the public**, and no build may claim they are, until all of the
following is done and real sign-in has been tested:

### Google (Gmail and Google Workspace)

1. A Google Cloud project owned by SidePhase Studio with an OAuth client of type *Desktop app*.
2. OAuth consent screen: app name **SidePhase Mail**, support email, logo, a homepage and a **privacy policy on a
   domain SidePhase Studio owns** (published at https://sidephasestudio.com/sidephasemail/privacy/; source is `PRIVACY.md`), authorised domains verified in Google Search Console.
3. **Brand verification** of the consent screen.
4. **Restricted scope verification** for `https://mail.google.com/` (needed for IMAP/SMTP): a justification and a
   demo video of the sign-in and how mail data is used.
5. **Security-assessment applicability** confirmed by Google's verification team for this local-only architecture.
   Server-side access can require an annual third-party assessment; restricted-scope verification still applies locally.
6. Until verified, the app is limited to up to 100 test users added by hand, and their refresh tokens expire after 7
   days. Gmail users can use an **app password** meanwhile.

### Microsoft (Outlook.com, Hotmail, Live, Microsoft 365)

1. An app registration in SidePhase Studio's Microsoft Entra tenant (multi-tenant + personal accounts), public client,
   redirect `http://localhost`, delegated `IMAP.AccessAsUser.All`, `SMTP.Send`, `offline_access`, `openid`, `email`.
2. **Publisher verification**: link the registration to SidePhase Studio's verified Microsoft Partner Network
   (Microsoft AI Cloud Partner Program) ID and a verified publisher domain. Without it, users see an *unverified*
   warning, and many organisations block user consent to unverified multi-tenant apps.
3. Work/school tenants: admins may need to grant consent, and must have IMAP and **SMTP AUTH** enabled for the
   mailbox (Microsoft turns SMTP AUTH off by default in many tenants).
4. Outlook.com/Hotmail no longer accept passwords for IMAP/SMTP, so these accounts need step 1 and 2 done first.

### Client IDs in builds

The Studio Google Desktop Client ID is a public identifier embedded in portable and Store builds from
`Directory.Build.props`; CI can override it with the `SIDEPHASE_GOOGLE_CLIENT_ID` Actions variable.
The required Google Desktop value comes from ignored local build configuration or an Actions secret; it is embedded in the desktop binary and is not confidential. Microsoft registration settings remain as documented above.
See [GOOGLE_SETUP.md](../GOOGLE_SETUP.md).

## Security decisions

* **No plaintext passwords or tokens on disk.** Secrets go only to the DPAPI-protected secret store; SQLite has
  no password or token columns (a test checks this). A damaged or foreign secret is treated as missing, so the user is asked to sign in again.
* **No secrets in logs or error messages.** Errors are translated into friendly text. Sign-in failures never include the server's reply; other refusals (e.g. a rejected recipient) may quote it, but passwords and tokens are never part of an error message.
* **Public registration only in builds.** The Studio Google Client ID is embedded; the issued Desktop client value is included as required by the live token endpoint. No confidential web-client credentials are used.
  Each user's OAuth tokens remain DPAPI-protected locally.
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
