# Account providers, OAuth and security (Milestone 2 plan)

Nothing in this document is implemented yet; it records the requirements the Milestone 2 work must meet.
OAuth client IDs will be **configurable** (never hard-coded or committed): a local, git-ignored
`*.local.json` file or environment variables at build/run time.

## Google (Gmail)

* Use OAuth 2.0 for **installed/desktop apps**: system browser + loopback redirect (`http://127.0.0.1:<port>`)
  with PKCE. Embedded web views are not allowed by Google for sign-in.
* Mail access via IMAP/SMTP uses the scope `https://mail.google.com/` (XOAUTH2). This is a **restricted scope**:
  public distribution requires Google's OAuth app verification **and** an annual third-party security
  assessment (CASA). Until then the app is limited to test users added in the Google Cloud console.
* The Gmail API (`gmail.modify`, also restricted) is an alternative with the same verification burden;
  IMAP keeps one engine for every provider, so it is the default plan.
* SidePhase Mail never asks for or stores a Google password.

Registration: Google Cloud console → APIs & Services → OAuth consent screen (External) → Credentials →
*Desktop app* client → put the client ID in local configuration.

## Microsoft (Outlook.com, Hotmail, Live, Microsoft 365)

* Register an app in Microsoft Entra ID (“Accounts in any organizational directory and personal Microsoft
  accounts”), public client, redirect URI `http://localhost` and the WAM broker redirect.
* Use MSAL.NET (with the Windows broker where available).
* Scopes: `https://outlook.office.com/IMAP.AccessAsUser.All`, `https://outlook.office.com/SMTP.Send`,
  `offline_access`. Microsoft Graph (`Mail.ReadWrite`, `Mail.Send`) is an option for richer features later.
* Microsoft 365 tenants may require **admin consent**, and SMTP AUTH may be disabled per mailbox; the UI
  must explain both clearly.

## Other providers

1. Thunderbird ISPDB (`autoconfig.thunderbird.net`), provider `autoconfig.<domain>` and
   `<domain>/.well-known/autoconfig`, then DNS SRV records (RFC 6186), then common host guesses.
2. Manual IMAP/SMTP entry.
3. TLS only (implicit TLS or STARTTLS required). The app must **never** silently fall back to plaintext or to a
   weaker authentication mechanism.

## Credentials

* Tokens and app passwords are encrypted with Windows data protection (DPAPI, current user) or stored in the
  Windows Credential Locker; never in `settings.json`, never in logs.
* Expired or revoked tokens trigger a non-blocking “Sign in again” prompt on the account.
* Removing an account deletes its local cache and credentials only — never server mail.
