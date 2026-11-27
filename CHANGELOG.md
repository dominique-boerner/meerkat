# Changelog

All notable changes to Meerkat are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The section of a version becomes the text of its GitHub release and is what Meerkat shows under
"What's new" when it offers the update.

## [Unreleased]

### Added

- Server widget: monitors Linux machines over SSH with reachability, CPU history, load, memory, disk
  usage and systemd services. It uses the OpenSSH client of Windows, reports unreachable hosts and
  nearly full disks, and blocks a host whose key changed until the new key has been verified. Hosts
  sign in through the SSH agent, a key file or a password kept in the Windows Credential Manager.

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
