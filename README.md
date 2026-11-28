<p align="center">
  <img src="assets/logo/meerkat-tile.svg" width="112" height="112" alt="Meerkat logo">
</p>

# Meerkat

[![CI](https://github.com/dominique-boerner/meerkat/actions/workflows/ci.yml/badge.svg)](https://github.com/dominique-boerner/meerkat/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/dominique-boerner/meerkat)](https://github.com/dominique-boerner/meerkat/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Meerkat keeps an eye on your development environment so you do not have to. A small status bar in the
corner of the screen shows whether your containers and pipelines are fine. It stays out of the way:
it never takes the keyboard focus from your editor, and as long as everything works it is one quiet
line.

When something breaks, the status bar says what and since when. A click on it, or a global hotkey,
opens a side panel with the details and the actions to deal with it.

Meerkat is a generic shell that hosts **widgets**. It ships with widgets for Docker containers running
inside WSL, for GitLab and GitHub, for servers reachable over SSH, for the status of external
services, and for notes.

## Features

**Shell**

- *Status bar*: one chip per widget, always on top; a click opens the panel without taking the
  keyboard focus away from the application you work in
- Errors appear as cards below the status bar with their age; a click on a card opens the page of the
  widget that reported it. Warnings are only counted, so the bar stays small
- *Panel*: a side panel, opened with a global hotkey, with one page per widget and badges for
  widgets that need attention; it stays open next to your work until you close it
- A second hotkey hides and shows Meerkat entirely; hidden widgets stop polling
- Tray icon with quick access to the panel, the settings and exit
- Per-monitor DPI aware; choose the monitor the status bar and the panel appear on
- A failing widget is suspended and can be restarted without affecting the shell or other widgets
- Updates itself from GitHub releases, with checksum verification and installation on request

**Docker widget**

- Lists all containers of a WSL distribution with status, CPU and memory usage
- Shows the containers of other machines as well, reached over SSH, one section per machine that
  can be folded up
- Start, stop and restart containers from the panel; other machines are read-only unless you allow it
- Live log viewer with text filter, stdout/stderr toggles, pause, and a bounded line buffer
- Warns about containers stuck in a restart loop and reports an unreachable WSL distribution, host
  or Docker daemon as an error
- Scans the images of your containers for known vulnerabilities with Trivy, once a day and whenever
  a new image appears, and reports critical ones with the image they come from and what fixes them

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

**Services widget**

- Watches the external services your work depends on, such as GitHub, npm, Docker Hub or Maven
  Central, by reading their status pages
- Shows a disturbance in the status bar with the incident, its latest update and the affected
  components, so a failing pull or push explains itself
- Also checks any address by its HTTP status, or any JSON endpoint by a value you name

**Notes widget**

- Notes as plain Markdown files in a folder of your choice, shown rendered and edited block by block
- A scratchpad that collects tasks and quick notes; a global hotkey opens a small input field above
  whatever you are working on, so you never have to leave it to write something down
- Quick notes are stamped with the time and grouped by day; completed tasks are tidied up on the
  next day
- Optionally keeps the tasks in view below the status bar

## Requirements

| | |
|---|---|
| Operating system | Windows 10 or 11, x64 |
| Runtime | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (not needed for the self-contained download) |
| Docker widget | WSL 2 with Docker Engine installed **inside** a distribution. Docker Desktop is not required. For other machines: the OpenSSH client of Windows and an SSH user who may use Docker on the host. |
| Git widget | A personal access token for GitLab (gitlab.com or self-managed) or GitHub (github.com or Enterprise Server) |
| Server widget | The OpenSSH client that ships with Windows (`ssh.exe`) and SSH access to the hosts; the hosts must run Linux |

## Installation

1. Download the latest archive from the [releases page](https://github.com/dominique-boerner/meerkat/releases/latest):
   - `Meerkat-<version>-win-x64.zip` requires the .NET 10 Desktop Runtime
   - `Meerkat-<version>-win-x64-self-contained.zip` bundles the runtime
2. Extract it anywhere and run `Meerkat.exe`.

Meerkat shows its status bar in the top right corner, from where you can move it anywhere, and adds
an icon to the notification area. There is no installer; to have Meerkat start with Windows, switch
that on in the settings under *General*.

## Usage

| Action | Default hotkey |
|---|---|
| Open or close the panel | `Ctrl+Alt+M` |
| Hide or show Meerkat | `Ctrl+Alt+H` |
| Write a quick note | `Ctrl+Alt+N` |
| Add a task | `Ctrl+Alt+T` |

Click the status bar to open or close the panel, or click a card to open the panel on the page it
belongs to. A card you have seen can be put away: click the cross that appears under the pointer,
or right-click it to mute it for an hour or until tomorrow. It stays
away until its alert is over or has something new to say. The chip in the bar keeps its colour
meanwhile, as an outline: filled means news, outlined means known. Drag the tabs of the panel up or down to change the order of the widgets; the chips in
the status bar follow it. Drag the status bar by the handle at its end to move it, for example away from the buttons of maximized windows;
its place is remembered relative to the screen, so it is the same on every monitor and resolution.
The panel stays open while you work in other windows. It closes when you press `Esc` inside it,
press the hotkey again or use its close button. Drag its left edge to change the width, for example to read long log
lines; a double-click on the edge restores the default. Both actions are also available from the tray icon.

### Configuration

Open the settings from the tray icon or the gear button in the panel. There is nothing to save:
every change is stored and applied the moment you make it.

- *General* holds what concerns Meerkat as a whole: starting with Windows, the opacity of the status
  bar, the monitor Meerkat appears on, all hotkeys, and updates.
- *Machines* is where other machines are entered, once for all widgets: name, address, user, port
  and how to sign in. See [Other machines](#other-machines).
- Every widget has a page of its own, with the switch that turns the widget on and off. A widget
  that is switched off offers no settings and no hotkeys until it is switched on again; what was set
  for it is kept.

Everything is stored as JSON in `%AppData%\Meerkat\settings.json`, which *Open settings.json* at the
bottom of the window opens. A few values are only found there, such as exact polling intervals
(`CustomListIntervalSeconds` and its siblings) or the number of log lines kept (`MaxLogLines`).
Meerkat takes over what you save in the file right away. Errors are logged to
`%AppData%\Meerkat\error.log`.

### Other machines

The Docker and Server widgets reach other machines over SSH, through the OpenSSH client of Windows,
so your `~/.ssh/config`, the SSH agent and `known_hosts` apply as they do in a terminal. A machine is
entered once on the *Machines* page of the settings; the pages of the widgets then only choose what
the machine is used for.

- Hosts from your `~/.ssh/config` are offered by name; for others enter host name, user and port.
- Meerkat signs in with the keys your SSH agent or configuration provides, with a key file you
  pick, or with a password. A password is stored in the Windows Credential Manager under
  `Meerkat:machine/...`, never in `settings.json`; where the machine allows it, a key is the safer
  choice.
- *Connect and check* verifies the entries with a first connection. That is also the moment the
  machine's key is stored in `known_hosts`. Afterwards Meerkat only connects while the key stays the
  same.
- Hosts entered in earlier versions, separately for Docker and Server, are moved to this list the
  first time the new version starts; a host both widgets knew becomes one machine.

### Updates

Meerkat keeps itself up to date. At startup and once a day it asks the GitHub API for the releases of
this repository, downloads a newer version in the background and verifies it against the published
SHA-256 checksum. Nothing is installed without your consent:

- A hint below the status bar and a dot on the tray icon show that an update is ready.
- A card in the panel offers to restart and install now, or to install when Meerkat exits.
- The *General* page of the settings shows the release notes and lets you turn off the automatic
  check or the background download and switch to the pre-release channel.

The update check contacts `api.github.com` and `github.com` and sends no data besides the request
itself. Apart from it, Meerkat only uses the network for the Git, Server and Services widgets once you have
configured them, and only to reach the servers you entered. Updating is only available in the
published single-file builds. Where `Meerkat.exe` lies in a folder only administrators may change,
such as `C:\Program Files`, Windows asks for administrator rights when the update is installed.

### Docker widget

The widget talks to Docker exclusively through `wsl.exe`, for example
`wsl -d <distribution> --exec docker ps -a --format json`. Select the distribution in the settings;
by default the WSL default distribution is used. The user Meerkat runs the commands as must be
allowed to use Docker without `sudo`.

*Refresh* sets how often Docker is asked: *Fast* lists containers every 2 seconds, *Normal* every 5
and *Economical* every 15. Other machines are asked less often on their own, every 10, 20 or 60
seconds, because every request opens an SSH connection.

To see the containers of another machine, enter it under [Machines](#other-machines) and add it
with *+ Machine* on the Docker page. Meerkat then runs the docker CLI on that machine.
Nothing is installed there unless you ask for it, and no port besides SSH is used.

- The user you sign in with must be allowed to use Docker there, which usually means being in the
  `docker` group. Bear in mind that this is close to root access to the host.
- A machine is read-only by default: you see containers, figures and logs, but cannot start, stop or
  restart anything. Allow that under *Edit* if you want it.

#### Scanning images for vulnerabilities

The *Security* tab of the Docker page lists the images your containers were created from and what
[Trivy](https://trivy.dev) finds in them. The scan runs on the machine the images are on, be it the
WSL distribution or another machine, so no image is copied anywhere.

- Meerkat uses a Trivy that is already installed. Where there is none, *Install Trivy* on the tab or
  in the settings downloads a fixed release from Trivy's GitHub releases, checks it against a
  checksum that is part of Meerkat, and puts it into `~/.local/share/meerkat/trivy` of the user
  Meerkat works as. That needs no root rights and changes nothing else on the machine; delete the
  folder to remove it again.
- All images are scanned once a day at the time set in the settings, and a new image as soon as it
  appears. This computer is not scanned while it runs on battery unless you ask for a scan.
- Another machine is only scanned if you switch that on under *Edit*. A scan takes some of its
  processor time, which is why it is off by default.
- Vulnerabilities from the severity you choose, critical by default, are reported in the status bar.
  Select an image on the tab to see which package to update to which version. By default only
  vulnerabilities with an available fix are shown, because the others leave nothing to do.
- The crossed-out eye next to a vulnerability ignores it: it is taken out of the counts and reports on every system. Ignored ones are
  listed in the settings and can be restored there.
- Results are kept in `%LocalAppData%\Meerkat\docker-image-scans.json` and can be deleted at any time.

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

Enter the machine under [Machines](#other-machines), then open the *Server* page of the settings
and add it with *+ Machine*. If the key of a machine changes later, the machine is blocked and the
panel shows the new fingerprint for you to verify.

Each check is a single SSH call that reads `/proc`, `df` and `systemctl is-active`. Nothing is
installed or changed on the machine. Per machine you can list the systemd services to watch and set
the disk usage at which Meerkat shows a hint or an error. *Refresh* sets how often the machines are
checked: every 15, 30 or 120 seconds.

### Services widget

Open the settings, choose *Services* and add a service, either one of the suggestions or your own
with one of three templates:

| Template | What is requested | How the state is read |
|---|---|---|
| Status page | `<address>/api/v2/summary.json` of a status page that offers the Statuspage API | The page's indicator, the current incident, its latest update and the components |
| HTTP status | The address as given | A success status code (2xx) is fine; any other code or no answer is an outage |
| Custom JSON | The address as given | A value at a path such as `status.indicator`, compared with the values you list for green and yellow; anything else is red |

*Test* sends the request once and shows what Meerkat makes of the answer. Not every status page
offers the Statuspage API, GitLab.com for one does not; use one of the other templates there.

A disturbance appears as a card in the status bar. *Mute until resolved* takes it out of the status
bar until the service is fine again. If an endpoint cannot be reached, the service keeps the state
last learned and the page says that the check failed; only for the *HTTP status* template that means
an outage. Each check is a single GET request, by default every five minutes, and nothing is sent
along.

### Notes widget

Every note is a Markdown file in `Documents\Meerkat\Notes`; the folder can be changed in the
settings. The page in the panel shows a note rendered. Click a block to edit its Markdown, click
elsewhere or press `Esc` to finish; check boxes can be ticked directly. Changes are saved right away,
and changes made to the files by other programs show up on their own.

The page can be used with the keyboard alone. While editing, `Ctrl+Enter` saves the block and starts
a new one right below it, `Tab` and `Shift+Tab` move to the next and the previous block, and so do the
arrow keys from the last and the first line. `Esc` ends the editing and leaves the block selected:
the arrow keys then move from block to block and `Enter` opens one again.

The *scratchpad* is always there and cannot be deleted. It has two sections that Meerkat writes to:

- `Ctrl+Alt+T` opens an input field for a task, which is appended to **Tasks**.
- `Ctrl+Alt+N` opens the same field for a quick note, which goes on top of **Quick notes** with the
  time of day. Start the text with `[]` or `- ` to make it a task instead.

Press `Enter` to save and `Esc` to discard. Below those two sections the scratchpad is yours; Meerkat
never changes anything there. If you remove one of the headings, it is created again with the next
entry. Tasks you completed stay visible for the rest of the day and move to `archive.md` on the next
one, or are deleted if you prefer that.

The pin button of the scratchpad shows its tasks below the status bar; a click on them opens the scratchpad.

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
| `src/Meerkat.Ssh` | SSH access through the OpenSSH client of Windows, shared by the Docker and Server widgets |
| `src/Meerkat.Widgets.Docker` | The Docker widget |
| `src/Meerkat.Widgets.Git` | The Git widget for GitLab and GitHub |
| `src/Meerkat.Widgets.Server` | The Server widget for machines reachable over SSH |
| `src/Meerkat.Widgets.Services` | The Services widget for the status of external services |
| `src/Meerkat.Widgets.Notes` | The Notes widget with the scratchpad |
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
