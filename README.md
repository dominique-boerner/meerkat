# Meerkat

[![CI](https://github.com/dominique-boerner/meerkat/actions/workflows/ci.yml/badge.svg)](https://github.com/dominique-boerner/meerkat/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/dominique-boerner/meerkat)](https://github.com/dominique-boerner/meerkat/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Meerkat is a desktop overlay for developers on Windows. It stays on top of your work without getting
in the way: in display mode every click passes straight through to the application underneath, and a
global hotkey turns the overlay interactive when you want to act on what you see.

The overlay is a generic shell that hosts **widgets**. The first widget watches the Docker containers
running inside WSL.

## Features

**Overlay shell**

- Borderless, transparent, always-on-top window that never takes focus from the application you work in
- Two modes, toggled with a global hotkey
  - *Display*: clicks pass through the overlay
  - *Interactive*: widgets can be operated, moved and resized
- A second hotkey shows and hides the whole overlay
- Tray icon with quick access to both modes, the settings and exit
- Per-monitor DPI aware; widget positions and sizes are stored per monitor
- A failing widget is suspended and can be restarted without affecting the shell or other widgets

**Docker widget**

- Lists all containers of a WSL distribution with status, CPU and memory usage
- Start, stop and restart containers in interactive mode
- Live log viewer with text filter, stdout/stderr toggles, pause, and a bounded line buffer
- Polling pauses while the overlay is hidden
- Dedicated error states when WSL, the Docker daemon or the Docker CLI is unavailable

## Requirements

| | |
|---|---|
| Operating system | Windows 10 or 11, x64 |
| Runtime | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (not needed for the self-contained download) |
| Docker widget | WSL 2 with Docker Engine installed **inside** a distribution. Docker Desktop is not required. |

## Installation

1. Download the latest archive from the [releases page](https://github.com/dominique-boerner/meerkat/releases/latest):
   - `Meerkat-<version>-win-x64.zip` requires the .NET 10 Desktop Runtime
   - `Meerkat-<version>-win-x64-self-contained.zip` bundles the runtime
2. Extract it anywhere and run `Meerkat.exe`.

Meerkat starts in display mode and adds an icon to the notification area.

## Usage

| Action | Default hotkey |
|---|---|
| Toggle between display and interactive mode | `Ctrl+Alt+O` |
| Show or hide the overlay | `Ctrl+Alt+H` |

In interactive mode, drag a widget by its header to move it and use the grip in its bottom-right
corner to resize it. The same actions are available from the tray icon.

### Configuration

Open the settings from the tray icon or the gear button of a widget. You can change the hotkeys,
the overlay opacity, the monitor the overlay is shown on, which widgets are enabled, and the
settings each widget contributes.

Everything is stored as JSON in `%AppData%\Meerkat\settings.json`. Errors are logged to
`%AppData%\Meerkat\error.log`.

### Docker widget

The widget talks to Docker exclusively through `wsl.exe`, for example
`wsl -d <distribution> --exec docker ps -a --format json`. Select the distribution in the settings;
by default the WSL default distribution is used. The user Meerkat runs the commands as must be
allowed to use Docker without `sudo`.

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or newer.

```powershell
git clone https://github.com/dominique-boerner/meerkat.git
cd meerkat
dotnet build Meerkat.slnx
dotnet test Meerkat.slnx
dotnet run --project src/Meerkat.App
```

### Project structure

| Project | Purpose |
|---|---|
| `src/Meerkat.App` | The overlay shell: window, hotkeys, tray icon, settings, widget hosting |
| `src/Meerkat.Abstractions` | Contracts and shared visual theme for widgets |
| `src/Meerkat.Widgets.Docker` | The Docker widget |
| `tests/Meerkat.Tests` | Unit tests (xUnit) |

### Test environment for the Docker widget

`deploy/wsl` contains a cloud-init configuration that provisions a dedicated WSL distribution with
Docker Engine and a few demo containers:

```powershell
./deploy/wsl/New-MeerkatWsl.ps1
```

Afterwards select the distribution `Meerkat` in the Docker widget settings. Remove it again with
`wsl --unregister Meerkat`.

## Writing widgets

Widgets implement a small interface and are registered with the shell through dependency injection.
See [Building widgets](docs/building-widgets.md) for a walkthrough.

## Releases

Releases are built by GitHub Actions. Pushing a tag of the form `v<major>.<minor>.<patch>` runs the
tests, publishes both archives with SHA-256 checksums and creates the GitHub release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

Tags with a suffix, such as `v1.0.0-beta.1`, are published as pre-releases. To produce the same
archives locally, run `./build/Publish-Release.ps1 -Version 1.0.0`.

## License

Meerkat is released under the [MIT License](LICENSE).
