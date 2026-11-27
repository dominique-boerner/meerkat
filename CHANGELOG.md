# Changelog

All notable changes to Meerkat are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The section of a version becomes the text of its GitHub release and is what Meerkat shows under
"What's new" when it offers the update.

## [Unreleased]

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
