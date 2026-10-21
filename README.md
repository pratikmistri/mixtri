# Mixtri

**Professional screen recording with cinematic cursor effects for Windows.**

Mixtri is a screen recorder and editor optimized to run natively on Windows. Record your screen, add beautiful effects, and export polished videos — all from a native Windows app.

> **Formerly Musio.** The app was renamed to Mixtri; it is the same project, and the Store listing
> updates in place. Projects saved as `.musio` still open — Mixtri saves new ones as `.mixtri`.

![Mixtri Screenshot](docs/screenshot.png)

## Features

- **Screen capture** — Record full screen, a single window, or a custom region at 30 or 60 fps
- **Cinematic cursor effects** — Automatic zoom-to-click, smooth cursor trails, and highlight animations
- **Timeline editor** — Trim, split, and adjust speed with a visual timeline and instant preview
- **Automatic typing pace** — Sustained text-entry bursts are accelerated to 1.5×, muted, and framed with a subtle caret-focused zoom (or a broad click-position fallback for custom text controls)
- **Segment transitions** — Configurable dissolve, wipe, slide, push, and stylized effects at any timeline cut
- **Beautiful backgrounds** — Gradient, image, and wallpaper backgrounds behind your recording
- **Webcam overlay** — Picture-in-picture webcam feed with shape and position controls
- **Flexible export** — Export to MP4, GIF, or WebM in resolutions up to 4K with preset management

## Prerequisites

| Tool | Version |
|------|---------|
| .NET SDK | 9.0+ |
| Windows 10 SDK | 10.0.26100.0+ |
| Visual Studio | 2022 17.8+ with **.NET desktop development** and **Windows App SDK** workloads |

## Build & Run

The simplest route is to open `Mixtri.sln` in Visual Studio and press **F5**.

From the command line, build with **Visual Studio MSBuild**:

```powershell
# Clone the repo
git clone https://github.com/pratikmistri/mixtri.git
cd mixtri

# Locate VS MSBuild
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -products * -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1

# Build (always pass /restore; a bare /t:Build fails to restore after a clean)
& $msbuild src\Mixtri.App\Mixtri.App.csproj /restore /t:Build /p:Configuration=Debug /p:Platform=x64
```

> **`dotnet build` does not work on this repo.** It fails in the WinUI resource (PriGen) tooling
> with `MSB4062`, as does `dotnet test` when it tries to build. Use VS MSBuild as above. The app
> is packaged (MSIX), so `dotnet run` is not the launch path either — run it from Visual Studio,
> or register the build output with
> `Add-AppxPackage -Register <bin>\AppxManifest.xml`.

## Microsoft Store release packages

The `release/1.6.0` branch uses package version **1.6.0.0** and displays **1.6.0** in
Settings. Before packaging, check the [live Store listing](https://apps.microsoft.com/detail/9PDRBRJL32H8)
and Partner Center for any newer published or pending version. Keep the manifest and Settings
versions synchronized; the fourth component of the Store package version must remain zero.

From the repository root, locate `$msbuild` as above, then build the architectures sequentially:

```powershell
$packageDir = Join-Path (Get-Location) 'src\Mixtri.App\AppPackages\1.6.0\'

# x64 = AMD64 / x86-64; ARM64 = native Windows on Arm.
foreach ($platform in 'x64', 'ARM64') {
    $rid = "win-$($platform.ToLowerInvariant())"
    & $msbuild 'src\Mixtri.App\Mixtri.App.csproj' /restore /t:Build `
        /p:Configuration=Release "/p:Platform=$platform" `
        "/p:RuntimeIdentifier=$rid" "/p:RuntimeIdentifiers=$rid" `
        /p:SelfContained=true /p:GenerateAppxPackageOnBuild=true `
        /p:AppxPackageSigningEnabled=false /p:AppxBundle=Never `
        /p:MixtriDevBranding=false "/p:AppxPackageDir=$packageDir" /nr:false
    if ($LASTEXITCODE -ne 0) {
        throw "Store packaging failed for $platform."
    }
}
```

The output is under `src\Mixtri.App\AppPackages\1.6.0\`. Submit the architecture-specific
unsigned `.msix` packages together in the same Partner Center submission; Microsoft signs them.
Before uploading, inspect each package as a ZIP: `AppxManifest.xml` must retain identity
`PratikMistri.Musio`, its existing publisher, version `1.6.0.0`, and the expected architecture.
Also verify the packaged executable's PE architecture and that `AppxSignature.p7x` is absent.
Do not submit a `.Dev` package or change the Store identity to match the display name.

## Tests

Build the test project with MSBuild first, then run it without rebuilding:

```powershell
& $msbuild src\Mixtri.Tests\Mixtri.Tests.csproj /restore /t:Build /p:Configuration=Debug /p:Platform=x64
dotnet test src\Mixtri.Tests\Mixtri.Tests.csproj --no-build -p:Platform=x64 -c Debug
```

If the host lacks the exact .NET 9 runtime, set `DOTNET_ROLL_FORWARD=Major` before `dotnet test`.

## Architecture

```
mixtri/
├── src/
│   ├── Mixtri.App/         # WinUI 3 front-end (XAML pages, view models, controls)
│   │   ├── Pages/          # RecordingPage, EditorPage, ExportPage, SettingsPage
│   │   ├── Controls/       # PreviewCanvas, TimelineControl, RegionSelector
│   │   ├── ViewModels/     # MVVM view models (CommunityToolkit.Mvvm)
│   │   ├── Helpers/        # DialogHelper and UI utilities
│   │   └── Services/       # App-level services
│   ├── Mixtri.Core/        # Platform-independent capture & editing engine
│   └── Mixtri.Tests/       # MSTest suite covering Mixtri.Core
├── docs/                   # Documentation and assets
├── learnings/              # Settled playbooks and the historical decision archive
└── Mixtri.sln              # Solution file
```

## License

This project is licensed under the Apache License, Version 2.0 — see the [LICENSE](LICENSE) file
for details.

If you redistribute Mixtri or a derivative work, the license requires you to retain copyright and
attribution notices and to include the attribution from the [NOTICE](NOTICE) file in your
redistribution (e.g., in a NOTICE file, documentation, or a credits screen). Please keep the
credit visible in your product's about/credits screen or documentation.

Releases published before this change remain available under the MIT License.
