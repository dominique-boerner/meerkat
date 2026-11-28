# Changelog

All notable changes to Meerkat are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The section of a version becomes the text of its GitHub release and is what Meerkat shows under
"What's new" when it offers the update.

## [Unreleased]

### Fixed

- Meerkat can update itself in a folder only administrators may change, such as `C:\Program Files`:
  Windows asks for administrator rights when the update is installed. Before, the update failed there.

## [1.7.0] - 2026-11-28

### Added

- The Docker widget scans the images of your containers for known vulnerabilities with Trivy. A new
  *Security* tab lists the images with their critical and high findings and, for a selected image,
  what to update to which version. Critical vulnerabilities are reported in the status bar with the
  affected images and, where it can be told, the base image that causes them. Images are scanned
  once a day and whenever a new one appears, on this computer and on any other machine you switch
  it on for.
- Trivy can be installed from Meerkat where it is missing: into the home directory of the user
  Meerkat works as, without root rights and checked against a checksum.
- Vulnerabilities can be ignored and restored, and the settings choose from which severity on they
  are reported, when the daily scan runs and whether those without a fix are shown.
- Widgets can add further entries to the status bar besides their main one.
- A *Machines* page in the settings, where other machines are entered once for all widgets. The
  Docker and Server pages only choose what a machine is used for. Hosts entered before are moved
  there on the first start; a host both widgets knew becomes one machine. An older version of
  Meerkat does not find them there, so they would have to be entered again after going back to one.
- *Open settings.json* in the settings window opens the settings file, and Meerkat takes over what
  is saved in it right away.

### Changed

- Settings are stored and applied the moment they are changed; the Save and Cancel buttons are gone.
- The settings have half as many pages: *General* now holds hotkeys and updates as well, and each
  widget is switched on and off on its own page, which replaces the *Widgets* page.
- Docker and Server ask for a pace instead of seconds: *Refresh* is fast, normal or economical, and
  other machines are asked less often on their own. Exact intervals and the number of log lines
  kept can still be set in the settings file.
- Explanations in the settings moved into tooltips of small info symbols.
- Widgets: a settings page stores its changes itself, so `IWidgetSettingsPage.Apply` is gone, and
  `IWidgetSettingsStore` announces settings that were changed from outside.

## [1.6.0] - 2026-11-27

### Added

- Services widget: watches external services such as GitHub, npm, Docker Hub or Maven Central by
  reading their status pages, and shows a disturbance with its incident, latest update and affected
  components. Any other address can be checked by its HTTP status or by a value in its JSON answer.
- The Docker widget shows the containers of other machines, reached over SSH, with figures and logs.
  Each machine gets its own section, which can be folded up, and is read-only unless starting and
  stopping is allowed for it.
- Meerkat can start when you sign in to Windows. Switch it on in the settings under *General*, the
  page formerly called *Appearance*.
- Widgets can mark a warning as prominent, which shows it as a yellow card in the status bar.
- The tabs of the panel can be dragged to change the order of the widgets, which the chips in the
  status bar follow.

### Changed

- Every widget page has the same kind of header in the same place, and headings, spacing and type
  sizes are the same across the pages.
- A widget that is switched off no longer offers a settings page or hotkeys; they return when it is
  switched on again.
- Notes can be written with the keyboard alone: `Ctrl+Enter` continues in a new block below, `Tab`,
  `Shift+Tab` and the arrow keys move between blocks, and after `Esc` the block stays selected so
  that `Enter` opens it again.

### Fixed

- The status bar is no longer cut off when many widgets report: it takes the room its chips need,
  and the cards widen with it.

## [1.5.0] - 2026-11-27

### Added

- Notes widget: notes as Markdown files, shown rendered and edited block by block. Its scratchpad
  collects tasks and quick notes, which you enter from anywhere with `Ctrl+Alt+T` and `Ctrl+Alt+N`.
  Completed tasks move to an archive note on the next day, and the tasks can be pinned below the
  status bar.
- Widgets can offer global hotkeys, which are listed and changed on the Hotkeys page, and can show a
  card below the status bar.

### Changed

- The status bar can be clicked: a click on the bar opens or closes the panel, a click on a card
  opens the panel on the page of the widget it belongs to. Clicking does not take the keyboard focus
  from the application you work in, and a slight highlight shows what can be clicked.
- The "Details" hint below the cards is gone. The hotkey is shown as a tooltip of the logo instead.
- The status bar can be dragged by a handle to any place on the screen. The place is stored relative
  to the screen size, and the cards open towards the middle of the screen: below or above the bar,
  to the left or to the right.

## [1.4.0] - 2026-11-27

### Added

- Server widget: monitors Linux machines over SSH with reachability, CPU history, load, memory, disk
  usage and systemd services. It uses the OpenSSH client of Windows, reports unreachable hosts and
  nearly full disks, and blocks a host whose key changed until the new key has been verified. Hosts
  sign in through the SSH agent, a key file or a password kept in the Windows Credential Manager.

### Changed

- The panel stays open when you click outside of it or switch to another window. It closes with
  `Esc`, the hotkey or its close button; the pin button is gone because it is no longer needed.

## [1.3.0] - 2026-11-27

### Changed

- New way of working with Meerkat: floating widgets are replaced by a small click-through status bar
  with one chip per widget and a side panel with the details, opened with `Ctrl+Alt+M`.
- The status bar shows errors as cards with their age and only counts warnings, so it stays out of
  the way while everything works.
- Widgets now report a status and alerts to the shell. The Docker widget warns about containers in a
  restart loop; the Git widget reports failed pipelines and waiting reviews.
- The log viewer of the Docker widget is part of its page in the panel again.
- The Git settings show the connection at a glance: a stored token is indicated by dots, the page
  connects on its own and lists all projects or repositories with a search field, and the token can
  be removed.
- Hotkeys are assigned by clicking a field and pressing the combination. Meerkat's own hotkeys are
  released while recording, so the current combinations can be pressed without triggering them.
- The panel can be made wider or narrower by dragging its left edge, which helps when reading logs.
  The width is remembered; double-clicking the edge restores the default.

### Removed

- Display and interactive mode, moving and resizing widgets, and the layouts stored per monitor.
  The hotkey for switching modes (`Ctrl+Alt+O`) is replaced by the hotkey for the panel.

## [1.2.0] - 2026-11-27

### Added

- Git widget for GitLab and GitHub: pipelines or workflow runs per branch, failed runs with their
  stages, job log and retry, and the merge or pull requests waiting for your review.
- Access tokens are stored in the Windows Credential Manager, not in the settings file.

## [1.1.0] - 2026-11-27

### Added

- Automatic updates: Meerkat checks GitHub for new releases at startup and once a day, downloads them
  in the background, verifies the published SHA-256 checksum and installs them on request or on exit.
- Update hint in the widget footer, an update card in interactive mode, a badge on the tray icon and
  an "Updates" page in the settings with release notes and a choice of release channel.

## [1.0.0] - 2026-11-27

### Added

- Overlay shell with display and interactive mode, global hotkeys, tray icon and per-monitor layouts.
- Docker widget for containers running in WSL with stats, actions and a live log viewer.
