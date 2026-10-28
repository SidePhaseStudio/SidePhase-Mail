# Installing SidePhase Mail (beta)

SidePhase Mail runs on Windows 10 (version 2004, build 19041) or later and Windows 11, x64. There are two builds;
use one of them, not both, because they keep separate data after the first start.

## Option 1: portable build (simplest)

1. Download the **portable** artifact (a .zip) from the release or build you were given.
2. Right-click the zip → *Properties* → tick **Unblock** if shown → *OK*, then extract it to a folder you own, for
   example `C:\Users\<you>\Apps\SidePhase Mail`. Don't extract into *Program Files*.
3. Run `SidePhaseMail.exe`. Windows SmartScreen may warn because the beta isn't signed: *More info* → *Run anyway*.

Data is kept in `%LOCALAPPDATA%\SidePhase Mail`. To update, replace the folder with the new version; your accounts,
cache and settings stay where they are.

## Option 2: installed package (MSIX)

Beta packages are **unsigned**, so Windows needs a certificate on your PC before it will install one.

1. Download and extract the **msix** artifact.
2. Open **PowerShell as administrator** in the extracted folder and run:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   .\Install-DevPackage.ps1 -Package .\SidePhase.Mail.App_0.7.0.0_x64.msix   # use the .msix file name in the folder
   ```

   The script creates a certificate that exists only on your PC, asks before trusting it, installs the Windows App
   Runtime it needs and then installs SidePhase Mail. It never uploads anything.
3. Start **SidePhase Mail** from the Start menu.

If you used the portable build before, the installed app copies its data once on first start (the original folder
is left untouched). Installing a newer beta package upgrades in place and keeps your accounts, cache and saved
sign-ins. Uninstalling (Settings → Apps) removes the app **and its data**.

To remove the test certificate later: `.\Install-DevPackage.ps1 -RemoveCertificate`.

## First start

* **Other / IMAP accounts** (including Gmail and Yahoo with an *app password*) are the tested way to add mail.
* **Sign in with Google / Microsoft** isn't available in beta builds unless SidePhase Studio has configured it; it
  hasn't been tested against the real providers yet.
* The demonstration mailbox (*Settings → Accounts*) is clearly labelled and never mixes with real accounts.

## Reporting problems

See [BETA_TESTING.md](BETA_TESTING.md#reporting-a-bug). *Settings → About* shows the version and has a link to the
bug report form. Logs (`Logs\app.log` in the data folder) never contain passwords, tokens or message content, but do
check before attaching them.
