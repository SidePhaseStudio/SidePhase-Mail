# SidePhase Mail privacy policy

Published policy: https://sidephasestudio.com/sidephasemail/privacy/

Effective / last updated: 29 October 2026.

## Who provides the app

SidePhase Mail is provided by SidePhase Studio. For support, privacy questions or requests about information you have sent us, contact [support@sidephasestudio.com](mailto:support@sidephasestudio.com). This policy covers the current SidePhase Mail beta and its product pages.

## The short version

Your mail accounts connect directly from your PC to the providers you choose. SidePhase Studio does not operate a service that receives or stores your mailbox, mail-account passwords or OAuth tokens. We do not sell Google user data, use it for advertising profiles, or send it to an AI service. You do not create a SidePhase Studio account to use the app.

## Google access and Limited Use

When you add Gmail, Google asks you to authorise mail access and confirmation of your email address. Mail access lets the app synchronise and display messages, fetch attachments, compose and send mail, save drafts, change flags, organise folders and permanently delete messages when you explicitly confirm that action. Google requires the full-mail scope for this IMAP/SMTP access. Ordinary Delete moves mail to Trash; confirmed permanent deletion cannot be undone through the app.

We comply with the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including its Limited Use requirements, when using or transferring information received from Google APIs. Google data is used only for the app’s visible email features. It is not sold, used for targeted advertising, used to determine creditworthiness, or used to train general-purpose AI models. SidePhase Studio has no routine access to read your Google messages. If you choose to share specific messages or diagnostics with support, we use that material to handle your request; please minimise and redact it.

## Information stored on your PC

The app stores account names, email addresses and connection settings; cached message headers, previews and downloaded bodies; downloaded or queued attachments; drafts, outbox items and search indexes; contacts, signatures, rules, categories, snoozes, pins and preferences. Optional local spam learning keeps word counts derived from messages you classify. This processing stays on the PC.

Passwords for password-based accounts and OAuth access/refresh tokens are stored separately using Windows DPAPI for the current Windows user. They are not stored in the mail database or preferences. The mail cache itself is not encrypted by the app: protect your Windows account, device and backups appropriately. Portable and installed editions keep separate app-data folders; the installed edition can copy portable data on first use.

## How information moves

Mail, account identifiers and authentication credentials are transmitted directly to your selected mail provider over TLS to provide mail functions. Google or Microsoft sign-in uses that provider’s own website in your browser; your provider password is not entered into SidePhase Mail. Tokens go directly to the provider when authenticating or refreshing access.

Account setup may contact your provider’s autoconfiguration service with your email address. The Mozilla Thunderbird settings database is queried with the email domain only when used as a fallback. If you permit remote images, the relevant image hosts receive normal network requests; those images are blocked by default. Opening external links uses your web browser and the destination’s policies apply.

## Promotions, analytics and diagnostics

The app has no SidePhase Studio analytics, tracking identifiers, automatic crash upload or automatic support-data upload. Local diagnostics are designed to exclude message contents, subjects, passwords and tokens, and to shorten addresses to their domain. Review logs before choosing to share them.

The beta can show bundled Studio promotions or a built-in preview banner. These do not use mail or contacts to choose content. A separate remote-promotion mode exists but is off unless explicitly configured: it requests a banner using placement, app version and display language, with normal network connection metadata. It sends no mailbox data, OAuth tokens, cookies or user identifier. Gmail data is never used for advertising. There is currently no in-app purchase available.

## Retention, removal and revocation

Your local data remains under your control while you use the app. Removing an account deletes its active cached mailbox and saved sign-in from that installation, without deleting server mail. Queued outbox messages are deliberately retained so unsent work is not lost. Separate contacts, crash-recovery files, saved attachments, database backups, portable copies and your own device backups can also remain. Account removal is not a secure erase of every historical copy.

Clear local data removes cached messages, which download again if the account is still connected; it does not sign you out. To remove all app-held local data, remove accounts, cancel unwanted queued messages, close the app and remove its app-data folder and any retained copies. Exported attachments and your backups must be removed separately. Revoke Google access independently in your Google Account’s third-party connections; revocation does not erase copies already downloaded to your PC. See [the data-removal instructions](/sidephasemail/support/#remove-data).

## Support correspondence and your choices

If you contact support, we receive the address, message and any material you choose to send. We use it to answer your enquiry, investigate the reported issue and keep necessary support records; we do not request your mailbox password or OAuth tokens. Where applicable, our basis for handling support correspondence is our legitimate interest in providing support, or a legal obligation. We keep it only as long as reasonably needed for those purposes and applicable obligations. Contact us to request access, correction or deletion, or to raise a privacy concern.

Your mailbox is held by you and your mail provider, so we cannot retrieve or erase local mailbox data on your behalf. You can control remote images, notifications and local spam learning in Settings, remove accounts and revoke provider permissions. Where applicable, you may also have rights to restriction, objection or portability and to complain to your data-protection authority, including the UK Information Commissioner’s Office.

## Website visits and third-party services

These product pages have no visitor accounts, forms, analytics scripts or advertising scripts. Their own code does not set cookies or use browser storage. Cloudflare and hosting/network services process connection information, such as IP addresses and request details, to deliver and protect the pages; security features may use their own cookies. See [Cloudflare’s privacy policy](https://www.cloudflare.com/privacypolicy/) and the [Studio website privacy notice](/privacy.html).

GitHub processes visits when you open project/download/issue links. Windows, WebView2, Windows Error Reporting and the Microsoft Store have their own privacy practices and settings. Windows may send diagnostics to Microsoft according to your system settings. These services do not give SidePhase Studio routine access to your mailbox.

## Children and policy changes

SidePhase Mail is a general-purpose email client and is not directed at children. We update this policy when the app’s handling of information changes. Material changes will be explained in release notes or the app as appropriate; any new use of Google data requiring consent will be presented before that use begins.
