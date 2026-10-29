# My Android Phone

A native Windows desktop application (C#, WinUI 3, Windows App SDK) for connecting to and managing Android devices through Android Debug Bridge (adb).

> **Status: early foundation (Stage 1 of 7).** The desktop shell, first-run wizard, tool detection, settings and diagnostics exist. **Device connectivity, file transfer, screen mirroring and app management are not implemented yet.** The UI does not show controls for them.

## Current implementation status

| Feature | Status | Verification |
|---|---|---|
| Setup wizard (Welcome, Android tools, License notice, Finish) | Implemented, progress persisted | Persistence logic exercised by a console harness on Linux. **WinUI UI not compiled or run.** |
| Automatic dependency download/install | **Not implemented** | The wizard detects tools and lets you select them; it never downloads or installs anything. |
| Desktop shell (menu bar, floating module windows, Dock, notifications) | Implemented | XAML checked for well-formedness only. **Not compiled or run.** |
| Tool Manager (detect, verify, select adb/scrcpy) | Implemented (no install/update/remove) | Detection, version parsing and validation exercised against stand-in `adb`/`scrcpy` scripts on Linux. Not run against real adb/scrcpy. |
| Settings (application) | Implemented: theme, Dock labels, log options, reset | Persistence exercised on Linux. UI not run. |
| Settings (device) | Not implemented | Needs device connectivity. |
| Logs and Diagnostics (view, redacted export) | Implemented | Report builder and rotating logger exercised on Linux. UI not run. |
| USB connection | **Not implemented** | |
| Wireless connection | **Not implemented** | |
| File Explorer | **Not implemented** | |
| Live Run (scrcpy) | **Not implemented** | |
| App Manager | **Not implemented** | |
| Transfer Center | **Not implemented** | |
| Release packaging | In progress | Project is configured for MSIX and unpackaged publish; **no package has been built.** |
| GitHub Actions build | Written, **never run** | Treat as unverified until a run succeeds. |

### What was actually verified

- `Core`, `Infrastructure` and `Tools` compile on .NET 8 with warnings as errors (on Linux).
- A throwaway console harness ran 43 checks against that code and all passed: process runner (argument handling without a shell, timeout and cancel kill the process, large stdout and stderr do not deadlock, missing executable), JSON settings and setup-state persistence (including corrupt-file recovery), tool detection and rejection of impostor executables, log rotation and redaction, diagnostics report, feature registry. The harness is not part of the repository.
- Not verified: the WinUI 3 app project (needs Windows to build), MSIX packaging, behaviour with real adb, scrcpy or Android devices, and the minimum Windows build below.

## System requirements

- Windows 10 version 1809 (build 17763) or later, or Windows 11; x64, x86 or ARM64 builds. The 1809 minimum is the value configured for the pinned Windows App SDK 1.x line and has **not** been re-confirmed against Microsoft's current documentation. Check it before releasing.
- Android SDK Platform-Tools (adb). scrcpy is optional and only needed for the future Run module.
- .NET 8 SDK and Visual Studio 2022 with the Windows application development workload to build.

## Setup and tools

On first launch the wizard checks for adb and scrcpy in: the app's tools folder (`%LOCALAPPDATA%\MyAndroidPhone\tools`), `ANDROID_HOME`/`ANDROID_SDK_ROOT`, `PATH` and the default Android Studio location. If you choose an executable manually, its file name must match (`adb.exe`, `scrcpy.exe`) and it is run once with its version option; the output must identify the tool before the path is saved. A saved path that later breaks is reported as invalid and is never silently replaced. The app does not change your PATH, does not request administrator rights, and sends no telemetry.

To get adb, download [Android SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools), extract it, and use **Locate executable** in Tool Manager.

## Build

```powershell
dotnet restore src/MyAndroidPhone.App/MyAndroidPhone.App.csproj -r win-x64
dotnet build MyAndroidPhone.sln -c Release -p:Platform=x64
```

Unpackaged self-contained publish (what CI produces):

```powershell
dotnet publish src/MyAndroidPhone.App/MyAndroidPhone.App.csproj -c Release -r win-x64 -p:Platform=x64 `
  -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:SelfContained=true -o artifacts/publish-x64
```

The workflow in `.github/workflows/build.yml` builds x64 and ARM64 on `windows-2022` and uploads the published folders and MSBuild binary logs as artifacts. Download them from the workflow run's **Artifacts** section. An MSIX needs a signing certificate whose subject matches `Publisher` in `Package.appxmanifest`; the placeholder identity there is for development only.

## Repository layout

| Project | Purpose |
|---|---|
| `MyAndroidPhone.Core` | Models, interfaces, results, feature descriptors, legal notices. No package dependencies. |
| `MyAndroidPhone.Infrastructure` | JSON persistence, rotating redacting logger, process runner, OS checks, diagnostics. |
| `MyAndroidPhone.Tools` | Tool detection, version parsing and validation. |
| `MyAndroidPhone.App` | WinUI 3 shell, wizard, view models, DI composition. |

Further feature projects (device, file explorer, run, app manager, transfer center) are added when their stage is implemented, not before.

### Adding a feature

1. Add a `FeatureDescriptor` (name, icon glyph, required tools, actions) in `BuiltInFeatures`.
2. Add its view and view model, register them in `App.xaml.cs`.
3. Add one entry in `ModuleViewFactory`.

The Dock and Modules menu are generated from the registry. A feature whose required tool is missing explains what is missing instead of opening.

## Dependencies

Pinned centrally in `Directory.Packages.props`, with the reason for each.

| Component | License | Notes |
|---|---|---|
| Microsoft.WindowsAppSDK 1.8.250907003 | See the NuGet package | WinUI 3 runtime. **This exact version string could not be checked against NuGet from the build environment; confirm it restores.** |
| CommunityToolkit.Mvvm 8.4.0 | MIT | MVVM source generators. |
| Microsoft.Extensions.DependencyInjection / Logging | MIT | DI and logging abstractions. |
| Android SDK Platform-Tools | Android SDK License Agreement | Not bundled. |
| scrcpy | Apache-2.0 | Not bundled. |

The application's own license has not been chosen. Add a `LICENSE` file before distributing.

## Privacy

Everything is local. No telemetry, no uploads. Logs are redacted for secret-looking values and the user profile path, and rotate by size. Diagnostics reports contain application, OS and tool information and recent warnings only.

## Known limitations

- No device features yet (see status table).
- Tool installation and updates are manual.
- Language infrastructure is not implemented; UI text is English only.
- Window size and position are not persisted.
- Keyboard navigation and accessibility have not been audited; the Dock buttons and close buttons carry automation names.
- Single-instance redirection (`Program.cs`) has not been run.

## Troubleshooting

- **Tool shows "needs attention":** the file was found but its version output did not match, or the selected file no longer exists. Use **Locate executable** or **Use automatic detection**.
- **Logs:** `%LOCALAPPDATA%\MyAndroidPhone\logs`, or Open logs folder in Logs and Diagnostics.
