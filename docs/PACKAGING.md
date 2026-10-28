# Packaging, installation and updates

SidePhase Mail is built two ways from the same project (`src/SidePhase.Mail.App`):

| | **MSIX package** (primary) | **Portable build** (development / fallback) |
| --- | --- | --- |
| How it's built | `-p:SidePhasePackage=msix` | default |
| Identity | Package identity `SidePhaseStudio.SidePhaseMail` (placeholder, see below) | none (unpackaged) |
| Start menu, icon, uninstall | Yes: Windows installs, lists and removes it | No: run `SidePhaseMail.exe` from the folder |
| Default email app (`mailto:`) | Yes (Settings → Apps → Default apps) | No |
| .NET runtime | Bundled in the package | Bundled in the folder |
| Windows App SDK runtime | `Microsoft.WindowsAppRuntime` framework package (dependency) | Bundled in the folder (self-contained) |
| Data folder | `%LOCALAPPDATA%\Packages\<package family>\LocalState` | `%LOCALAPPDATA%\SidePhase Mail` |
| Updates | Microsoft Store, or App Installer for signed beta packages | Manual (download a new folder) |
| CI artifact | `SidePhaseMail-<version>-win-x64-msix` (unsigned) | `SidePhaseMail-<version>-win-x64-portable` |

**Why MSIX:** it is what the Microsoft Store requires (with Store-managed updates and signing), it gives proper
package identity (needed for clean Start-menu integration, `mailto:` registration, notifications with identity), and
Windows installs and removes it cleanly. The Windows App SDK supports single-project MSIX packaging, so no separate
packaging project is needed.

## Building

```powershell
# Portable (unpackaged, self-contained folder)
dotnet publish src/SidePhase.Mail.App -c Release -r win-x64 -p:Platform=x64 -o artifacts\SidePhaseMail-win-x64

# MSIX (unsigned, sideload layout incl. Dependencies\ and install scripts) -> artifacts\msix\
dotnet build src/SidePhase.Mail.App -c Release -r win-x64 -p:Platform=x64 -p:SidePhasePackage=msix

# MSIX for a Store submission (.msixupload) - only after the identity placeholders are replaced
dotnet build src/SidePhase.Mail.App -c Release -r win-x64 -p:Platform=x64 -p:SidePhasePackage=msix -p:UapAppxPackageBuildMode=StoreUpload
```

In Visual Studio, the *SidePhase Mail (Unpackaged)* launch profile runs the portable build. To debug the packaged
app, build once with `-p:SidePhasePackage=msix` or add a `MsixPackage` launch profile locally.

### Versions

One version is used everywhere: `<VersionPrefix>` (and `<VersionSuffix>`, e.g. `beta.1`) in `Directory.Build.props`.
The package's `Identity Version` in `Package.appxmanifest` must be `VersionPrefix.0` (the Store requires the last
number to be 0); `PackagingTests` fails the build if they drift apart. CI adds the commit to the informational version
(`0.7.0-beta.1+<sha>`), shown in *Settings → About*.

### Identity placeholders (must be replaced before Store submission)

`Package.appxmanifest` uses placeholders, and nothing in this repository is a real certificate or registration:

* `Identity Name="SidePhaseStudio.SidePhaseMail"` and `Publisher="CN=SidePhase Studio (development)"`: replace with
  the values Partner Center shows under *Product management → Product identity* once the name **SidePhase Mail** is
  reserved. (Visual Studio's *Associate App with the Store* does this automatically.)
* Images in `Assets/Package/` are generated from `Assets/AppLogo.png` and should be replaced with final artwork
  (Square44x44 incl. target sizes, Square150x150, Wide310x150, StoreLogo, SplashScreen; 100% and 200% scales).

> **Important:** a package's identity (Name + Publisher) decides which app Windows thinks it is. A beta sideloaded
> with the placeholder identity and the Store app (with Partner Center's identity) are *different apps* to Windows:
> they install side by side and keep separate data. Reserve the Store name **before** a wide beta and use the real
> identity for beta packages too, so testers' installs upgrade in place.

## Signing

Windows only installs signed MSIX packages.

* **Microsoft Store builds** are signed by the Store. Nothing to do.
* **CI and beta builds are unsigned.** No signing key exists in the repository or in GitHub Actions. The build
  workflow's install test signs a *throwaway copy* with a certificate created on the runner for that test only; the
  uploaded artifact is the unsigned package.
* **To install an unsigned beta package on a test PC**, run `scripts/packaging/Install-DevPackage.ps1` (included in
  the MSIX artifact) from an administrator PowerShell:

  ```powershell
  .\Install-DevPackage.ps1 -Package .\SidePhase.Mail.App_0.7.0.0_x64.msix
  ```

  It creates a self-signed certificate **on that PC only** (subject = the manifest's Publisher, private key not
  exportable), trusts it for package installation (`LocalMachine\TrustedPeople`, asks first), signs the package with
  `signtool` (Windows SDK), installs the Windows App Runtime from `Dependencies\x64\` and installs the app.
  Remove the certificate afterwards with `.\Install-DevPackage.ps1 -RemoveCertificate`.
* **Signed beta releases outside the Store** need a real code-signing certificate issued to SidePhase Studio (or
  Azure Trusted Signing). The build can use one via `-p:PackageCertificateThumbprint=<thumbprint>` (certificate in
  the build machine's store) or `-p:PackageCertificateKeyFile=<pfx> -p:PackageCertificatePassword=...`. Keep it in a
  secure secret store; **never commit it** and never upload it as an artifact. The beta release workflow signs only
  if such secrets are configured (`SIGNING_CERTIFICATE_BASE64`, `SIGNING_CERTIFICATE_PASSWORD`), otherwise it
  publishes an unsigned draft.

## Runtime dependencies

* **.NET 10**: bundled (self-contained) in both builds.
* **Windows App SDK 2.5 runtime**: the portable build bundles it. The MSIX declares a dependency on the
  `Microsoft.WindowsAppRuntime` framework package. The Store installs it automatically; the sideload layout includes
  it in `Dependencies\x64\`, which `Install-DevPackage.ps1` installs. If Windows reports a missing dependency,
  install the Windows App Runtime redistributable from Microsoft's
  [Windows App SDK downloads](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads).
* **Microsoft Edge WebView2 Runtime**: part of Windows 11. Without it, message bodies fall back to plain text.

## Where data lives, and moving from the portable build

The two builds deliberately use **separate data folders**:

* Portable: `%LOCALAPPDATA%\SidePhase Mail\`
* Installed: `%LOCALAPPDATA%\Packages\<package family name>\LocalState\` (`ApplicationData.LocalFolder`)

Both hold the same files (`settings.json`, `mail-cache.db`, `Secrets\`, `Recovery\`, `Outbox\`, `Backups\`,
`Logs\`, `WebView2\`). The installed app does **not** use the portable folder directly: packaged apps' writes to
`AppData` are redirected by Windows (file-system virtualisation), so sharing it would split SQLite's database and
journal files between two places and could corrupt the cache, and uninstalling would delete the redirected half.

Instead, **the first time the installed app starts it copies the portable data** (`PortableDataImporter`):

* It runs only if the installed app has no data yet and the portable folder has some; it never overwrites anything,
  and runs once (a marker file `imported-from-portable.json` records it).
* It is a copy: the portable folder is not changed or deleted, so the portable build keeps working with its own data.
* Settings, accounts and the mail cache (copied with SQLite's backup API, so it is consistent even if the portable
  build is running; checked with `quick_check`), saved passwords and OAuth tokens (DPAPI-encrypted for the same
  Windows user, so they decrypt in either location), crash-recovery drafts and queued outbox items with their
  attachments are copied. Logs, the WebView2 cache and old backups are not.
* All or nothing: files are staged in `.import-staging` and moved into place only when everything copied. If it
  fails, the installed app starts empty, says so, and tries again next start.

After the copy the two builds are independent: changes in one don't appear in the other. Moving back from the
installed app to the portable build isn't automatic; copy the files from `LocalState` back yourself if needed.

**Data across updates.** MSIX updates (Store or App Installer) keep `LocalState`. Schema upgrades of the mail cache
take a backup first (`Backups\mail-cache-schema<N>-<time>.db`, three kept) and run in a transaction; a database from a
newer version is never modified. **Uninstalling removes `LocalState`** (Windows' normal behaviour for packaged apps),
including the cache, saved passwords and local-only items such as scheduled messages. Mail on the server is not
affected.

## Updates

| Channel | Mechanism | Notes |
| --- | --- | --- |
| Microsoft Store | Store-managed updates | SidePhase Mail has **no updater of its own**, so nothing competes with the Store. |
| Signed beta (outside the Store) | **App Installer** (`.appinstaller` file published with GitHub Releases) | Windows checks the `.appinstaller` URL and updates the package (on launch, at most every 24 h). Requires a signed package; the URL must be HTTPS. Generated by the beta release workflow. |
| Unsigned beta / portable | Manual | Download the new artifact. *Settings → About* shows the installed version and commit. |

Choices made:

* **No custom updater.** The app never downloads or runs installers. Updates come only from the Store or Windows'
  own App Installer, which verifies the package signature before installing.
* **Trusted sources only.** The `.appinstaller` file points at `https://github.com/SidePhaseStudio/SidePhase-Mail/releases/...`
  over HTTPS; App Installer refuses packages whose signature or identity doesn't match.
* **Store builds are never pointed at App Installer**, and the app doesn't show update prompts in Store builds.

## Uninstalling

* MSIX: *Settings → Apps → Installed apps → SidePhase Mail → Uninstall*. Windows removes the app and its
  `LocalState` data. The CI install test checks that the package installs, starts, and uninstalls cleanly.
* Portable: delete the folder, and `%LOCALAPPDATA%\SidePhase Mail` if you want to remove its data too.
