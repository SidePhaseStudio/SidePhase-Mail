# Changelog

All notable changes to SidePhase Mail. Versions follow `MAJOR.MINOR.PATCH-beta.N`; the MSIX package version is
`MAJOR.MINOR.PATCH.0` (see [docs/PACKAGING.md](docs/PACKAGING.md#versions)). Each beta raises the version so installed
packages upgrade in place and keep their data.

Status words follow [RELEASE_READINESS.md](RELEASE_READINESS.md): automated tests and CI passing do **not** mean a
feature has been checked by hand on Windows.

## Unreleased

### Changed
- Attachments are found with the normal search: typing a file name (or part of one) finds the messages it's attached
  to; `filename:` and `filetype:` still work. The separate *Attachments* page in the sidebar is gone.
- *Add account* moved from the sidebar to the top of *Settings → Accounts*.

### Fixed
- A message opened by double-click now comes to the front instead of opening behind the main window.
- Opening a message in an *Unread* smart folder or with the *Unread* filter no longer marks every message in the view
  as read. The open message now stays listed until you move to another one.
- Long messages can be scrolled with the mouse wheel and touchpad again (an invisible layer over the message was
  taking the scroll).

### Added
- After saving an attachment, the notice offers **Open** (through Windows, so SmartScreen and Office Protected View
  still apply). Files that can run programs or macros offer **Show in folder** instead. *Save all* offers **Open folder**.
- Double-click a message to open it in its own window (a draft opens for editing). The window follows read/star
  changes and closes if the message is deleted or moved away.

## 0.7.0-beta.1 — Milestone 7: final polish and controlled beta

### Fixed
- Message actions (move, archive, delete, junk, drag to a folder) only confirm, and only offer Undo for, what the
  server actually did. A refused action keeps the server's error on screen; partial results say how many moved.
- Forwarding a message with attachments on a real account now includes the attachments (downloaded first, never
  opened). Attachments that can't be fetched are listed and left out instead of failing silently.
- An offline or unreachable mail server no longer counts as a failed send: the message stays queued, retries with
  growing gaps (up to 10 minutes) and is sent **once** when the connection returns.
- Opening a message while offline no longer flips it to read and back with an error.
- Compose: autosave keeps retrying after a failure; Cc/Bcc can't be hidden while they contain addresses; files
  dropped onto the message text are attached; pasted images are attached instead of disappearing.
- Clicking a notification while SidePhase Mail is already open opens the message.

### Added
- Offline bar; on reconnecting, mail syncs, queued messages go, pending Sent copies are filed and drafts written
  offline are uploaded once.
- Account problems show as a warning on the account in the sidebar; the sign-in prompt opens *Settings → Accounts*.
- Named account colours with a visible selection; mailbox storage where the server reports it (IMAP QUOTA).
- One promotion banner centred in the footer (about 728 × 90), labelled *Promotion*, showing only approved SidePhase
  Studio promotions bundled with the app (none approved yet, so the footer stays hidden). Replaces the sidebar and
  reading-pane cards. No ad SDKs, no tracking.

### Accessibility
- Spellcheck in the subject and message; compose status announced to screen readers; visible focus outline in
  compose; system colours for caption buttons in high contrast; the reading-pane fade respects Windows *Animation
  effects*; message rows have spoken names.

### Docs
- Install guide, known issues, release notes, Store submission checklist, privacy draft updated for promotions.

## 0.6.0-beta.1 — Milestone 6: stabilisation and packaging
- Data-safety audit and fixes (single instance, UIDVALIDITY checks, durable Sent copies, stable Message-IDs,
  database backups before upgrades, settings that are never overwritten when unreadable).
- MSIX package (placeholder identity) alongside the portable build; one-time copy of portable data.
- Beta release workflow (draft pre-release only), send log for diagnosing delivery problems, privacy and security docs.

## 0.5 — Milestone 5: smart inbox
- Local full-text search, smart folders, local spam learning, rules, multi-select, bulk actions and drag and drop.

## 0.4 — Milestone 4: productivity
- Snooze, follow-up reminders, outbox with scheduled send and Undo Send, conversation view, local contacts.

## 0.3 — Milestone 3: message management
- Pins, favourites, SpamAssassin detection, Junk handling, search operators, notifications.

## 0.2 — Milestone 2: real accounts
- IMAP/SMTP via MailKit, SQLite cache, encrypted credentials, account setup, Google/Microsoft OAuth (untested).

## 0.1 — Milestone 1: working application
- WinUI 3 app with the Dark Glass design system, demo mailbox, compose window and settings.
