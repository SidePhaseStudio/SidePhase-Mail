# SidePhase Mail 0.7.0-beta.1 (controlled beta)

This is a **controlled beta** for invited testers using IMAP/SMTP accounts (including Gmail and Yahoo with an app
password). It isn't a public or production release.

## What's new
* **Honest message actions.** If the server refuses a move, delete or junk, you see the server's reason, not "Done".
  Undo only covers what actually moved.
* **Works offline.** An offline bar shows when you're disconnected. Messages you send wait in the Outbox and go once,
  automatically, when you're back. Drafts written offline are saved to the server when you reconnect.
* **Forwarding with attachments** works on real accounts.
* **Accounts.** Warnings on accounts that need attention, named colours, mailbox storage where supported.
* **Compose.** Spellcheck, drag files onto the message, paste screenshots as attachments.
* **Accessibility.** Better screen-reader labels and announcements, visible focus, high-contrast caption buttons,
  respects Windows animation settings.
* **Promotions.** One labelled banner position in the footer for SidePhase Studio promotions. None are switched on in
  this build, so you won't see it. It never uses your mail or contacts and never appears in your inbox or messages.

## Before you install
* Read [INSTALL.md](INSTALL.md) — beta packages are unsigned.
* Read [KNOWN_ISSUES.md](KNOWN_ISSUES.md), in particular the open "sent but not arriving" report.
* Use the [beta checklist](BETA_TESTING.md#checklist) and report problems with the bug report form.

Full list of changes: [CHANGELOG.md](../CHANGELOG.md).
