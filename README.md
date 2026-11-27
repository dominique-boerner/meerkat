<p align="center">
  <img src="assets/logo/meerkat-tile.svg" width="112" height="112" alt="Meerkat logo">
</p>

# Meerkat

[![CI](https://github.com/dominique-boerner/meerkat/actions/workflows/ci.yml/badge.svg)](https://github.com/dominique-boerner/meerkat/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/dominique-boerner/meerkat)](https://github.com/dominique-boerner/meerkat/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Meerkat keeps an eye on your development environment so you do not have to. A small status bar in the
corner of the screen shows whether your containers and pipelines are fine. It stays out of the way:
every click passes straight through it, and as long as everything works it is one quiet line.

When something breaks, the status bar says what and since when. A global hotkey opens a side panel
with the details and the actions to deal with it.

Meerkat is a generic shell that hosts **widgets**. It ships with widgets for Docker containers running
inside WSL, for GitLab and GitHub, and for servers reachable over SSH.

## Features

**Shell**

- *Status bar*: one chip per widget, always on top, click-through, never takes focus
- Errors appear as cards below the status bar with their age; warnings are only counted, so the bar
  stays small
- *Panel*: a side panel, opened with a global hotkey, with one page per widget, badges for widgets
  that need attention, and a pin to keep it open
- A second hotkey hides and shows Meerkat entirely; hidden widgets stop polling
- Tray icon with quick access to the panel, the settings and exit
- Per-monitor DPI aware; choose the monitor the status bar and the panel appear on
- A failing widget is suspended and can be restarted without affecting the shell or other widgets
- Updates itself from GitHub releases, with checksum verification and installation on request

**Docker widget**

- Lists all containers of a WSL distribution with status, CPU and memory usage
- Start, stop and restart containers from the panel
- Live log viewer with text filter, stdout/stderr toggles, pause, and a bounded line buffer
- Warns about containers stuck in a restart loop and reports an unreachable WSL distribution or
  Docker daemon as an error

**Git widget**

- Shows the pipelines of your GitLab projects or the workflow runs of your GitHub repositories,
  one row per branch, with status and age
- Explains failed runs with their stages, a link to the job log and a retry button
- Lists merge or pull requests waiting for your review and the ones you opened
- One layout for both providers; only the vocabulary changes
- Reports failed pipelines as an error and waiting reviews as a hint
- Keeps the last data visible when the token expires or a rate limit is hit, and says so

**Server widget**

- Monitors Linux machines over SSH: reachability, CPU with a 30-minute history, load, memory, disk
  usage and the state of systemd services
- Uses the OpenSSH client of Windows, so your `~/.ssh/config`, SSH agent and `known_hosts` apply
- Reports unreachable hosts and nearly full disks, and refuses to connect when a host key changed
  until you have verified the new one
- Opens a terminal session on a host with one click

## Requirements

| | |
|---|---|
| Operating system | Windows 10 or 11, x64 |
| Runtime | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (not needed for the self-contained download) |
| Docker widget | WSL 2 with Docker Engine installed **inside** a distribution. Docker Desktop is not required. |
| Git widget | A personal access token for GitLab (gitlab.com or self-managed) or GitHub (github.com or Enterprise Server) |
| Server widget | The OpenSSH client that ships with Windows (`ssh.exe`) and key-based access to the hosts; the hosts must run Linux |

## Installation

1. Download the latest archive from the [releases page](https://github.com/dominique-boerner/meerkat/releases/latest):
   - `Meerkat-<version>-win-x64.zip` requires the .NET 10 Desktop Runtime
   - `Meerkat-<version>-win-x64-self-contained.zip` bundles the runtime
2. Extract it anywhere and run `Meerkat.exe`.

Meerkat shows its status bar in the top right corner and adds an icon to the notification area.

## Usage

| Action | Default hotkey |
|---|---|
| Open or close the panel | `Ctrl+Alt+M` |
| Hide or show Meerkat | `Ctrl+Alt+H` |

The panel closes when you press `Esc`, press the hotkey again or switch to another window. Pin it to
keep it open next to your work. Drag its left edge to change the width, for example to read long log
lines; a double-click on the edge restores the default. Both actions are also available from the tray icon.

### Configuration

Open the settings from the tray icon or the gear button in the panel. You can change the hotkeys,
the opacity of the status bar, the monitor Meerkat appears on, which widgets are enabled, and the
settings each widget contributes.

Everything is stored as JSON in `%AppData%\Meerkat\settings.json`. Errors are logged to
`%AppData%\Meerkat\error.log`.

### Updates

Meerkat keeps itself up to date. At startup and once a day it asks the GitHub API for the releases of
this repository, downloads a newer version in the background and verifies it against the published
SHA-256 checksum. Nothing is installed without your consent:

- A hint below the status bar and a dot on the tray icon show that an update is ready.
- A card in the panel offers to restart and install now, or to install when Meerkat exits.
- The *Updates* page of the settings shows the release notes and lets you turn off the automatic
  check or the background download and switch to the pre-release channel.

The update check contacts `api.github.com` and `github.com` and sends no data besides the request
itself. Apart from it, Meerkat only uses the network for the Git and Server widgets once you have
configured them, and only to reach the servers you entered. Updating requires write access to the
folder `Meerkat.exe` is in, and is only available in the published single-file builds.

### Docker widget

The widget talks to Docker exclusively through `wsl.exe`, for example
`wsl -d <distribution> --exec docker ps -a --format json`. Select the distribution in the settings;
by default the WSL default distribution is used. The user Meerkat runs the commands as must be
allowed to use Docker without `sudo`.

### Git widget

Open the settings, choose *Git*, pick the provider and enter a personal access token:

| Provider | Token | Needed for |
|---|---|---|
| GitLab | Scope `read_api` | Everything except retrying pipelines, which needs `api` |
| GitHub | Classic token with scopes `repo` and `workflow` | Private repositories and re-running failed jobs |

*Test connection* signs in and loads your projects; tick the ones to watch. For a self-managed
GitLab or GitHub Enterprise Server, enter its address as server URL. Only `https` addresses are
accepted, because the token is sent with every request.

The token is stored in the Windows Credential Manager under `Meerkat:git/...`, never in
`settings.json`. The widget only talks to the server you configured.

### Server widget

Open the settings, choose *Server* and add a host. Hosts from your `~/.ssh/config` are offered by
name; for others enter host name, user and port. Meerkat signs in with the keys your SSH agent or
configuration provides, with a key file you pick, or with a password. A password is stored in the
Windows Credential Manager under `Meerkat:server/...`, never in `settings.json`; where the host
allows it, a key is the safer choice.

*Connect and check* verifies the entries with a first connection. That is also the moment the host's
key is stored in `known_hosts`. Afterwards Meerkat only connects while the key stays the same; if it
changes, the host is blocked and the panel shows the new fingerprint for you to verify.

Each check is a single SSH call that reads `/proc`, `df` and `systemctl is-active`. Nothing is
installed or changed on the host. Per host you can list the systemd services to watch, set the check
interval and the disk usage at which Meerkat shows a hint or an error.

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
| `src/Meerkat.App` | The shell: status bar, panel, hotkeys, tray icon, settings, updates, widget hosting |
| `src/Meerkat.Abstractions` | Contracts and shared visual theme for widgets |
| `src/Meerkat.Widgets.Docker` | The Docker widget |
| `src/Meerkat.Widgets.Git` | The Git widget for GitLab and GitHub |
| `src/Meerkat.Widgets.Server` | The Server widget for machines reachable over SSH |
| `tests/Meerkat.Tests` | Unit tests (xUnit) |

### Test environment for the Docker widget

`deploy/wsl` contains a cloud-init configuration that provisions a dedicated WSL distribution with
Docker Engine and a few demo containers:

```powershell
./deploy/wsl/New-MeerkatWsl.ps1
```

Afterwards select the distribution `Meerkat` in the Docker widget settings. Remove it again with
`wsl --unregister Meerkat`.

### Demo data for the Git widget

Set the environment variable `MEERKAT_GIT_DEMO=1` before starting Meerkat to fill the Git widget with
made-up pipelines and reviews. No account and no network access are needed, which helps when working
on the layout or taking screenshots.

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

Before tagging, move the entries under `Unreleased` in [CHANGELOG.md](CHANGELOG.md) to a section for
the new version. That section becomes the release text and is what the in-app updater shows.

Tags with a suffix, such as `v1.0.0-beta.1`, are published as pre-releases. To produce the same
archives locally, run `./build/Publish-Release.ps1 -Version 1.0.0`.

## License

Meerkat is released under the [MIT License](LICENSE).
