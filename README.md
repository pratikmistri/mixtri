# Musio

**Professional screen recording with cinematic cursor effects for Windows.**

Musio is a Screen Studio–inspired screen recorder and editor built with WinUI 3 and .NET 9. Record your screen, add beautiful effects, and export polished videos — all from a native Windows app.

![Musio Screenshot](docs/screenshot-placeholder.png)

## Features

- **Screen capture** — Record full screen, a single window, or a custom region at 30 or 60 fps
- **Cinematic cursor effects** — Automatic zoom-to-click, smooth cursor trails, and highlight animations
- **Timeline editor** — Trim, split, and adjust speed with a visual timeline and instant preview
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

```bash
# Clone the repo
git clone https://github.com/your-org/musio.git
cd musio

# Restore and build
dotnet restore
dotnet build src\Musio.App\Musio.App.csproj

# Run (requires Windows 10 1809+)
dotnet run --project src\Musio.App\Musio.App.csproj
```

Or open `Musio.sln` in Visual Studio and press **F5**.

## Architecture

```
musio/
├── src/
│   ├── Musio.App/          # WinUI 3 front-end (XAML pages, view models, controls)
│   │   ├── Pages/          # RecordingPage, EditorPage, ExportPage, SettingsPage
│   │   ├── Controls/       # PreviewCanvas, TimelineControl, RegionSelector
│   │   ├── ViewModels/     # MVVM view models (CommunityToolkit.Mvvm)
│   │   ├── Helpers/        # DialogHelper and UI utilities
│   │   └── Services/       # App-level services
│   └── Musio.Core/         # Platform-independent capture & editing engine
├── docs/                   # Documentation and assets
└── Musio.sln               # Solution file
```

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
