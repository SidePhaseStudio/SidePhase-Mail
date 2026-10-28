# Building SidePhase Mail

## Prerequisites

| Requirement | Notes |
| --- | --- |
| Windows 10 1809+ or Windows 11 (x64 or ARM64) | WinUI 3 apps build and run on Windows only. |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | `global.json` accepts any 10.0.1xx or later feature band. |
| Visual Studio 2022 17.14+ or Visual Studio 2026 *(optional)* | Workloads: **.NET desktop development** with **WinUI application development** (Windows App SDK C# templates). The CLI alone is enough to build. |
| Microsoft Edge WebView2 Runtime | Pre-installed on Windows 11; [download](https://developer.microsoft.com/microsoft-edge/webview2/) for Windows 10. Only needed to *run* the app. |

All NuGet packages (Windows App SDK 2.5, CommunityToolkit.Mvvm, …) restore automatically. Nothing needs
to be installed system-wide, and no secrets are required to build.

## Build and run from the command line

```powershell
git clone https://github.com/SidePhaseStudio/SidePhase-Mail.git
cd SidePhase-Mail

# Run the unit tests (also works on macOS/Linux)
dotnet test tests/SidePhase.Mail.Tests

# Build and launch the app (Debug, x64)
dotnet build src/SidePhase.Mail.App -p:Platform=x64
.\src\SidePhase.Mail.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\SidePhaseMail.exe
```

On an ARM64 PC use `-p:Platform=ARM64` (the output folder is then `...\win-arm64\`).

### Integration tests against a test mail server

The IMAP/SMTP integration tests are skipped unless a **disposable** test server is configured. On Ubuntu 24.04,
`scripts/ci/setup-test-mail-server.sh` sets one up (Dovecot + Postfix on 127.0.0.1, users `alice@example.test` /
`bob@example.test`). Then:

```bash
SIDEPHASE_TEST_IMAP_HOST=127.0.0.1 SIDEPHASE_TEST_IMAP_PORT=1993 \
SIDEPHASE_TEST_SMTP_PORT=1587 SIDEPHASE_TEST_SMTP_SECURITY=starttls \
dotnet test tests/SidePhase.Mail.Tests
```

Never point these variables at a real mailbox: the tests send, move and delete messages.

### OAuth client IDs for local builds

No OAuth client IDs or secrets are in the repository. To try Google or Microsoft sign-in, register your own apps
([docs/ACCOUNT_PROVIDERS.md](docs/ACCOUNT_PROVIDERS.md)) and enter the IDs in *Settings → Accounts*, or set
`SIDEPHASE_GOOGLE_CLIENT_ID`, `SIDEPHASE_GOOGLE_CLIENT_SECRET` and `SIDEPHASE_MICROSOFT_CLIENT_ID` before starting the app.

## Build in Visual Studio

1. Open `SidePhase.Mail.sln`.
2. Select the **x64** (or **ARM64**) solution platform and **SidePhase.Mail.App** as the start-up project.
3. Press **F5**. The launch profile is *SidePhase Mail (Unpackaged)*; no MSIX deployment or developer mode is required.

## Create an MSIX package

```powershell
dotnet build src/SidePhase.Mail.App -c Release -r win-x64 -p:Platform=x64 -p:SidePhasePackage=msix
```

The unsigned package and its sideload layout (with `Dependencies\` and install scripts) land in `artifacts\msix\`.
Signing, identity, data location and updates are described in [docs/PACKAGING.md](docs/PACKAGING.md).

## Versions

`VersionPrefix` / `VersionSuffix` in `Directory.Build.props` set the version of the app, its assemblies and the MSIX
package (the manifest's `Identity Version` must be `VersionPrefix.0`; a unit test checks it). Raise `VersionPrefix`
for every beta release.

## Create a portable build (what CI produces)

```powershell
dotnet publish src/SidePhase.Mail.App -c Release -r win-x64 -p:Platform=x64 -o artifacts\SidePhaseMail-win-x64
```

The output folder is self-contained: it bundles .NET and the Windows App SDK runtime, so it can be zipped
and copied to another PC and started with `SidePhaseMail.exe`. Release builds are ReadyToRun-compiled for
faster start-up. Only the WebView2 Runtime must be present on the target PC.

## Beta releases

*Actions → Beta release (draft) → Run workflow* runs the whole Build workflow (all tests, both packages, the MSIX
install test) and, only if everything passes, creates a **draft** pre-release `v<version>` with the portable zip, the
MSIX, a sideload zip, SHA-256 checksums and release notes. Nothing is published: review the draft and publish it by
hand. The MSIX is signed (and an App Installer file added) only if `SIGNING_CERTIFICATE_BASE64` and
`SIGNING_CERTIFICATE_PASSWORD` secrets are configured; otherwise it stays unsigned.

## Continuous integration

`.github/workflows/build.yml` runs on every push and pull request. The **Mail engine integration** job (Ubuntu 24.04)
starts the throwaway Dovecot/Postfix server and runs all tests including the IMAP/SMTP integration tests, with
both SSL/TLS and STARTTLS. The **Windows x64** job on `windows-latest`:

1. Restores packages.
2. Runs the xUnit test suite (results uploaded as the `test-results` artifact).
3. Publishes the app for win-x64 (self-contained, unpackaged, ReadyToRun).
4. **Verifies** that `SidePhaseMail.exe`, the app/engine assemblies, the WinUI and WebView2 assemblies,
   `SidePhaseMail.pri` (compiled XAML resources) and the icon exist — the job fails if anything is missing.
5. Uploads the runnable folder as `SidePhaseMail-<version>-win-x64-portable` (kept 30 days).
6. Builds the MSIX, checks its manifest and contents, then installs it (signed with a throwaway certificate created
   on the runner), starts it, checks that it copied the portable data into its own folder, and uninstalls it.
7. Uploads the unsigned package as `SidePhaseMail-<version>-win-x64-msix`. On failure the MSBuild binary logs are
   uploaded as `build-logs` (open them with the [MSBuild Structured Log Viewer](https://msbuildlog.com/)).

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `NETSDK1083` / runtime identifier errors | Pass `-p:Platform=x64` (or ARM64). The app project picks its runtime identifier from the platform. |
| “XamlCompiler.exe … could not be run” on macOS/Linux | Expected: the WinUI XAML compiler is Windows-only. Core, Infrastructure and Tests build anywhere. |
| App starts but message bodies show as plain text | The WebView2 Runtime is missing; install it from the link above. |
| Glass looks solid | Windows *Settings → Personalisation → Colours → Transparency effects* is off, a contrast theme is on, or a Solid theme is selected. This is intentional. |
| Where is data stored? | Portable: `%LOCALAPPDATA%\SidePhase Mail\`; installed: `%LOCALAPPDATA%\Packages\<package>\LocalState\` (Settings → About → Data folder). `settings.json`, `mail-cache.db` (SQLite cache), `Secrets\` (DPAPI-encrypted credentials), `Logs\` (never message contents or credentials). |
