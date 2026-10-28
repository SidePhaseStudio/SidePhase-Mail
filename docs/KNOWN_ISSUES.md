# Known issues (0.7.0-beta.1)

The full, evidence-based list is in [RELEASE_READINESS.md](../RELEASE_READINESS.md#known-issues). In short:

## Being investigated
* **Messages shown as sent but not arriving and not in Sent** were reported on one real account. Not reproduced
  against the test server. If it happens to you, please send the `send:` lines from `Logs\app.log` (they contain no
  addresses or content); see *Diagnosing a send problem* in [BETA_TESTING.md](BETA_TESTING.md).

## Not available yet
* Google and Microsoft sign-in haven't been tested with the real providers. Use an app password meanwhile.
* No push (IMAP IDLE): new mail arrives on the sync interval.
* First sync downloads the newest 500 messages per folder; use *Search on server* for older mail.
* Signatures are plain text. Pasted images are sent as attachments, not inline.
* Message bodies and attachments are available offline only if they were opened while online.

## Limitations
* Scheduled send, Undo Send, snooze, reminders and rules need SidePhase Mail to be running.
* Snoozes, reminders, pins, categories and spam learning are local to this PC.
* Drafts written in another app open without their attachments.
* The installed and portable builds keep separate data after the first copy.
* Pasted images and forwarded attachments are kept in the data folder (`Pasted`, `Forwarding`) and not cleaned up yet.
* A flag change made during a sync can occasionally be undone by that sync; it's corrected on the next change.
* Proton Mail Bridge (self-signed certificate) isn't supported.
* Most of the interface has been checked by building and starting it in CI, not yet by hand on many PCs, DPI
  settings, monitors, Narrator or high-contrast themes. That's what this beta is for.
