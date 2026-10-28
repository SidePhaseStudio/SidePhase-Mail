# Beta testing SidePhase Mail

Thank you for testing. This is a **beta**: please use it alongside your usual mail app, not instead of it, and read
the [release readiness notes](../RELEASE_READINESS.md) for what is and isn't verified yet.

**Never put real message contents, passwords or other people's addresses in a bug report.** The app's logs are
designed not to contain them (see *Logs* below), but please glance through anything you attach.

## Before you start

* Windows 11 x64 (Windows 10 2004+ works but isn't the target).
* An IMAP/SMTP account you're happy to test with. Google and Microsoft sign-in need OAuth client IDs that beta builds
  don't include, so test Gmail with an **app password** and Outlook.com only if you have a registered client ID.
* Download either the **MSIX** (installed app) or the **portable** zip from the release or the CI artifacts.

## Checklist

Tick each item, and report anything that fails or surprises you (*Reporting a bug* below). For each item, note the
build (Settings → About shows version, build and channel).

### Installation and start-up
- [ ] **Fresh install (MSIX):** run `Install-DevPackage.ps1` as administrator (unsigned beta) or open the signed
      package. SidePhase Mail appears in the Start menu with its icon.
- [ ] **Portable:** extract the zip and start `SidePhaseMail.exe`. (SmartScreen may warn: More info → Run anyway.)
- [ ] First start shows the welcome screen; the window opens centred and can be resized, maximised and moved
      between monitors (including different scaling, e.g. 100% and 150%).
- [ ] Starting it a second time brings the open window to the front instead of opening another copy.
- [ ] Light and dark Windows themes, *Transparency effects* off, and a contrast theme all look right.
- [ ] Moving from portable to installed: accounts and settings appear in the installed app; a notice says they
      were copied; the portable build still works with its own copy.

### Accounts
- [ ] **Custom IMAP account:** Add account → Other → address → settings found or filled in → password → test →
      saved. Try a wrong password: nothing is saved and the error is clear.
- [ ] **Gmail (app password):** works; *Sign in with Google* says it isn't configured (unless you added a client ID).
- [ ] **Outlook / Microsoft 365:** only with your own client ID (Settings → Accounts); otherwise the page explains why.
- [ ] **Thunderbird import (optional):** *Use settings from Thunderbird* asks first, fills in the server settings,
      and asks you for the password. Your Thunderbird profile is unchanged.
- [ ] Rename and recolour an account; restart; sign-in still works.
- [ ] **Account removal:** Settings → Accounts → Remove: the account and its cached mail disappear from the app;
      nothing is deleted on the server (check webmail).

### Sending and receiving
- [ ] Send a message to yourself and to another address. With Undo Send on (default 10 s) you see "Sending… Undo",
      then "Message sent." **It arrives, and appears once in your Sent folder** (in SidePhase Mail and in webmail).
- [ ] Undo a send within the window: it reopens for editing and is not delivered.
- [ ] Schedule a message for 2 minutes ahead: it's sent on time (the app must be open).
- [ ] Reply, reply all and forward keep the conversation together in the recipient's mail app.
- [ ] Turn Wi-Fi off and send: the message waits in the outbox and goes when you're back online.

### Reading, folders and organisation
- [ ] New mail arrives on the check interval; a Windows notification opens the message.
- [ ] **Folder synchronisation:** a change in webmail (read, star, move, delete) shows in SidePhase Mail after the
      next check, and changes in SidePhase Mail show in webmail.
- [ ] **Message deletion:** Delete moves to Trash; undo restores it; deleting from Trash removes it permanently
      (with confirmation); nothing else is affected.
- [ ] Pin, favourite, snooze, follow-up, categories, multi-select and drag-to-folder work and survive a restart.
- [ ] Search finds recent mail; *Search on server* finds older mail.
- [ ] HTML messages render; remote images are blocked until you allow them for that message; links ask first.

### Attachments
- [ ] Open a message with attachments; preview an image; save one file and *Save all*.
- [ ] Saving a `.exe`, `.js` or `.docm` attachment warns you first; nothing is ever opened automatically.
- [ ] A large attachment (10 MB+) downloads with progress and opens correctly afterwards in its own app.
- [ ] Attach files to a new message by picker and by drag and drop.

### Offline and resilience
- [ ] Start the app with no network: cached mail is readable, and a clear "offline" message appears; when the
      network returns, sync resumes without restarting.
- [ ] Close the app while a message is sending (hard to time; try a large attachment): on the next start the
      message is shown as *Uncertain* in the outbox, never resent automatically.

### Updates and uninstall
- [ ] **Application upgrade:** install a newer beta over the old one (MSIX): accounts, settings, pins, scheduled
      messages and cached mail are all still there.
- [ ] **Uninstallation:** Settings → Apps → SidePhase Mail → Uninstall removes it and its Start menu entry. (This
      also removes the installed app's local data; your mail on the server is not affected.)

### Optional
- [ ] Settings → About → *Preview sponsored cards*: a small "Sponsored · Test content" card appears at the bottom of
      the sidebar and in the empty reading pane, never in a message. *Hide* and *Simulate the ad-free upgrade* remove it.
- [ ] Set SidePhase Mail as the default email app (Settings → Apps → Default apps, installed app only) and click a
      `mailto:` link on a web page: a new message opens with the address filled in.

## Reporting a bug

1. Check the [open issues](https://github.com/SidePhaseStudio/SidePhase-Mail/issues) first.
2. Open a new issue with the **Bug report** template (Settings → About → *Report a problem* goes there).
3. Include: the build from Settings → About, Windows version, account type (Gmail / Outlook / other IMAP, but not
   the address), what you did, what you expected and what happened, and whether it happens again.
4. Attach logs if relevant (below). **Don't paste message contents, subjects, passwords or other people's addresses.**
5. For a security problem, don't open a public issue: use GitHub's *Report a vulnerability* (Security tab).

### Logs

Settings → About → *Data folder → Open* opens the app's data folder. Logs are in `Logs\`:

| File | Contains |
| --- | --- |
| `app.log` | Non-fatal problems and the **send trail** (outbox states, the mail server's reply, where the Sent copy went). |
| `errors.log` | Unexpected errors the app recovered from, with stack traces. |
| `startup-error.log` | Start-up failures. |

The logs never include message contents or subjects, passwords or tokens, and email addresses are reduced to
`***@domain`. They stay on your PC; nothing is uploaded automatically.

### Diagnosing a send problem ("sent but not arriving")

1. Send a test message to yourself.
2. Open the **Outbox** page in the sidebar: the message should be *Accepted*. If it's still *Queued* or
   *Sending*, the outbox isn't running: restart the app and check `app.log` for an `outbox` line.
3. In `Logs\app.log`, find the `send:` lines for that time. You should see
   `accepted by smtp.yourprovider:587, reply "250 …"` and `Sent copy in "Sent": appended as UID …`.
   * No `accepted by` line: the message never reached the server.
   * Accepted but it never arrives: the server took it and then didn't deliver it. Check webmail's Sent folder and
     for a bounce ("Undelivered Mail Returned to Sender") in your inbox. Include the `250` reply in your report: it
     has the provider's queue ID.
   * Accepted but no Sent copy: the line after it says why (or a notice was shown in the app).
