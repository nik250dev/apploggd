# Changelog

## Unreleased

### Added

- Support for RetroArch, Dolphin, Cemu
- Support for Game Boy, Game Boy Color, Game Boy Advance, DS, 3DS, NES, SNES, Nintendo 64, GameCube, Wii, Wii U, Mega Drive, Game Gear, Dreamcast, PlayStation, PS2, PS3, PSP, PS Vita, Xbox and Xbox 360
- Apps and emulated games can be added to a blacklist, so they are no longer detected
- Support for Linux
- The session confirmation can be handled with a controller (Settings > General > Controller navigation)
- Sessions can be left in the new Pending section to review later, from the clock button of the confirmation or the save error notice
- New setting to send every session to Pending without asking (Settings > General)

### Changed

- The tray notice is redesigned: it shows what detection is doing and appears next to the tray on any desktop layout

### Fixed

- The NVIDIA App overlay is no longer detected as a game
- Updating from a read-only folder now warns to move the app instead of failing
- Games that share an executable name with other games are now detected correctly
- The session confirmation no longer stays hidden behind Steam Big Picture after closing a game

## 1.1.0 — 2026-09-10

### Added

- Apploggd now updates itself, from the notice in Settings > About, the startup toast or the tray
- The app version is now shown at the bottom of the sidebar, under the logout button

### Changed

- Versions in the "What's new" window are now separated by a rule

### Fixed

- Restored compatibility for the "saving session" flow after an upstream UI change.
- Reduced resource usage of the headless browser
- Animations no longer run while minimized or in the tray

## 1.0.0 — 2026-08-09

First release of Apploggd.

### Features

- Detection of running games
- Session logging to Backloggd, plus a list of recently played games
- Automatic updates to game detection database
