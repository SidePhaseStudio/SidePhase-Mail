# SidePhase Mail privacy policy (DRAFT)

> **Draft for review. Not yet published.** It describes SidePhase Mail 0.6 (beta) as built from this repository,
> checked against the code. It must be reviewed (including legally) and published on SidePhase Studio's own website
> before a public release, and updated whenever the app's data handling changes, for example if sponsored content
> is ever switched on.

**Who we are.** SidePhase Mail is an email app for Windows made by SidePhase Studio. Contact: *[SidePhase Studio contact
address to be added]*.

## The short version

* Your email, contacts, passwords and settings **stay on your PC** and go only to **the mail providers you add**
  (for example your Gmail, Outlook or other mail server).
* SidePhase Studio **does not run any server that receives your email or your data**. The app has no analytics, no
  telemetry, no crash reporting to us, no ad tracking and no account with us.
* Some features contact other services, listed below, and only when you use them.

## What SidePhase Mail stores on your PC

In the app's data folder (`%LOCALAPPDATA%\SidePhase Mail`, or the installed app's own package folder):

* **Your mail:** copies of message headers, previews, the messages you open and their attachments when you save them,
  drafts, messages waiting to be sent, and a search index of this mail.
* **Account settings:** names, email addresses, server names and ports. **Passwords and sign-in tokens** are stored
  separately, encrypted with Windows (DPAPI), so only your Windows user account on this PC can decrypt them.
* **Things you create:** contacts (and people you've written to, for suggestions), pins, favourites, snoozes,
  reminders, categories, rules, smart folders, signatures, preferences.
* **Optional local spam learning:** if you mark messages as junk or not junk, word counts from those messages'
  sender domain, subject and preview are kept locally to improve suggestions. You can turn it off and reset it.
* **Diagnostic logs:** errors and a record of sending steps (such as the mail server's reply), kept on your PC to
  help diagnose problems. They're designed to exclude message contents, subjects, passwords and tokens; email
  addresses are shortened to the domain. Nothing is sent to us. You can share them if you choose to report a bug.

Uninstalling the installed app removes its data folder. For the portable version, delete the folder yourself.
Removing an account in the app deletes that account's local data. Mail on your provider's server is never deleted by
removing an account or uninstalling.

## Which services the app contacts, and when

| Service | When | What is sent |
| --- | --- | --- |
| **Your mail providers** (IMAP and SMTP servers you configure) | To sync, read, organise and send mail | What any mail app sends: your sign-in and the mail you read, send or change. |
| **Google / Microsoft sign-in** (accounts.google.com, oauth2.googleapis.com, login.microsoftonline.com) | When you sign in with Google or Microsoft, and to renew access in the background | The standard OAuth requests. Your password goes only to Google or Microsoft in your browser, never to the app. |
| **Your email provider's setup service** (`autoconfig.<your domain>` or `<your domain>/.well-known/autoconfig`) | When you add an account and its settings aren't built in | Your email address, to look up its server settings (the standard "autoconfig" method). |
| **Mozilla's mail settings database** (autoconfig.thunderbird.net) | When you add an account and the steps above found nothing | **Only the domain** of your email address (for example `example.com`), not the full address. |
| **Websites in messages** | Only when you allow remote images for a message, or open a link after confirming | Requests for those images, or the page you open, as any browser would. Remote images are blocked by default, are fetched without cookies, and never from addresses on your local network. |
| **GitHub** (github.com) | Only if you open the project or bug-report links in Settings, or install a beta update through Windows App Installer | Ordinary web requests. |

**Not contacted:** SidePhase Studio servers (none exist for the app), advertising networks, analytics services.

## Windows and Microsoft components

SidePhase Mail uses parts of Windows that have their own privacy settings, controlled by Windows rather than by us:
the **Microsoft Edge WebView2** runtime (used to display email safely), **Windows notifications**, **Windows Error
Reporting** (Windows may send crash reports to Microsoft according to your Windows diagnostic settings), and, for the
Store version, the **Microsoft Store** (installation, updates and, in future, purchases). See Microsoft's privacy
statement for these.

## Sponsored content

The app contains a framework for small sponsored cards, but **no advertising is shown and no advertising service is
contacted** in this version. A setting can display built-in test cards, which are part of the app and contact nothing.
If sponsored content is introduced, it will never use your email, contacts, address or anything about you, will never
appear inside your messages, and this policy will be updated first.

## Your choices

* Remove an account, or clear the local cache (Settings → Privacy), at any time.
* Turn off remote images (the default), local spam learning, and notifications.
* Look at, copy or delete the data folder (Settings → About → Data folder).

## Children

SidePhase Mail is a general-purpose email client and isn't directed at children.

## Changes

We'll update this policy when the app's data handling changes, and say what changed in the release notes.

*Last updated: [date of publication]*
