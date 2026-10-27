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

## Build in Visual Studio

1. Open `SidePhase.Mail.sln`.
2. Select the **x64** (or **ARM64**) solution platform and **SidePhase.Mail.App** as the start-up project.
3. Press **F5**. The launch profile is *SidePhase Mail (Unpackaged)*; no MSIX deployment or developer mode is required.

## Create a portable build (what CI produces)

```powershell
dotnet publish src/SidePhase.Mail.App -c Release -r win-x64 -p:Platform=x64 -o artifacts\SidePhaseMail-win-x64
```

The output folder is self-contained: it bundles .NET and the Windows App SDK runtime, so it can be zipped
and copied to another PC and started with `SidePhaseMail.exe`. Release builds are ReadyToRun-compiled for
faster start-up. Only the WebView2 Runtime must be present on the target PC.

## Continuous integration

`.github/workflows/build.yml` runs on every push and pull request on a GitHub-hosted `windows-latest` runner:

1. Restores packages.
2. Runs the xUnit test suite (results uploaded as the `test-results` artifact).
3. Publishes the app for win-x64 (self-contained, unpackaged, ReadyToRun).
4. **Verifies** that `SidePhaseMail.exe`, the app/engine assemblies, the WinUI and WebView2 assemblies,
   `SidePhaseMail.pri` (compiled XAML resources) and the icon exist — the job fails if anything is missing.
5. Uploads the runnable folder as the `SidePhaseMail-win-x64` artifact (kept 30 days). On failure the
   MSBuild binary log is uploaded as `build-logs` (open it with the [MSBuild Structured Log Viewer](https://msbuildlog.com/)).

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `NETSDK1083` / runtime identifier errors | Pass `-p:Platform=x64` (or ARM64). The app project picks its runtime identifier from the platform. |
| “XamlCompiler.exe … could not be run” on macOS/Linux | Expected: the WinUI XAML compiler is Windows-only. Core, Infrastructure and Tests build anywhere. |
| App starts but message bodies show as plain text | The WebView2 Runtime is missing; install it from the link above. |
| Glass looks solid | Windows *Settings → Personalisation → Colours → Transparency effects* is off, a contrast theme is on, or a Solid theme is selected. This is intentional. |
| Where are settings stored? | `%LOCALAPPDATA%\SidePhase Mail\settings.json`. Errors are logged to `%LOCALAPPDATA%\SidePhase Mail\Logs\errors.log` (never message contents or credentials). |
