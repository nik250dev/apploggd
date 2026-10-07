<div align="center">

<img src="app/BackloggdMirror/Assets/app-logo.png" alt="Apploggd" width="96" />

# Apploggd

**A game scrobbler for [Backloggd](https://backloggd.com).**

Tired of manually logging every play session on Backloggd? Me too :)

Apploggd automatically detects what you're playing, times the session and logs the playtime
to your Backloggd journal when you close the game.

![Version](https://img.shields.io/badge/version-1.2.0-8b5cf6)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-0078d4)
![.NET](https://img.shields.io/badge/.NET-8.0-512bd4)

</div>

---
<img width="1202" height="692" alt="image" src="https://github.com/user-attachments/assets/e8d690d2-daa9-46a5-bba9-3ab22925b7c4" />


## Contents

- [What it is](#what-it-is)
- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [How it works under the hood](#how-it-works-under-the-hood)
- [Privacy and data](#privacy-and-data)
- [Troubleshooting](#troubleshooting)
- [Known limitations](#known-limitations)
- [Credits](#credits)

## What it is

Apploggd lives in the system tray, watches which processes and windows you have open and
recognizes your running games. When you close the game, it lets you log the play session to your
Backloggd journal without ever opening a browser. There's nothing to configure beyond signing in
once.

**This app is not meant to be a 1:1 Backloggd client. Its goal is to automate logging play
sessions to your account.**

## Features

- **Automatic game detection** — by executable name (game database) and, as a fallback, by window
  heuristics (known game engines, fullscreen windows, and so on). On Linux, Steam games (native or
  through Proton) are recognized by their Steam App ID.
- **Emulated games** — RetroArch, Dolphin, Cemu, PPSSPP, DuckStation and PCSX2 are supported, so the
  session is logged for the game actually running in the emulator, across 20+ consoles (Game Boy,
  DS, 3DS, NES, SNES, Nintendo 64, GameCube, Wii, Wii U, Mega Drive, Dreamcast, PlayStation, PS2,
  PS3, PSP, PS Vita, Xbox, Xbox 360...).
- **Manual picker** — if a game isn't recognized, you can search Backloggd from within the app and
  pick the right one.
- **Playtime accumulates per day** — if you play several sessions of the same game on the same
  day, they're added to the existing entry instead of overwriting it.
- **Started / finished** — a session can be marked as the day the game was started or finished,
  like Backloggd's own play session options.
- **Pending sessions** — sessions can be left in a Pending section to review and save later, on the
  day they were played. There's also a setting to send every session there without asking.
- **Blacklist** — apps and emulated games that shouldn't be detected can be added to a blacklist.
- **Controller support** — the whole app can be used with a controller, without leaving the couch.
- **Offline mode** — with a remembered session, the app keeps working without a connection and
  reconnects on its own.
- **"Recently played" list** with your latest journal entries.
- **Self-updating** — both the app and its detection database update themselves.

## Requirements

| | |
|---|---|
| **Operating system** | Windows 10/11 or Linux (x64). *(macOS is not supported yet)* |
| **Browser** | A Chromium-based one (Chrome, Edge, Chromium). If there isn't one, the app will offer to download its own (~400 MB). |
| **Account** | A [Backloggd](https://backloggd.com) account. |

## Installation

There's no installer: Apploggd is portable on both systems. Get the latest version from the
[releases page](https://github.com/nik250dev/apploggd/releases).

### Windows

1. Download `Apploggd-v<version>-win-x64.zip`.
2. Extract it wherever you like. It expands into an `Apploggd` folder — keep its contents as they
   come: `Apploggd.exe`, `Update.exe` and `current\` all have to stay together for the app to be
   able to update itself.
3. Run `Apploggd.exe`.

### Linux

1. Download `Apploggd-v<version>-linux-x64.zip`.
2. Extract it wherever you like. It expands into an `Apploggd` folder with `Apploggd.AppImage`
   inside, already executable. Updates rewrite that same file in place.
3. Run `Apploggd.AppImage`.

## How it works under the hood

Backloggd **has no public API**, and that fact explains most of this project's design decisions.

### 1. The detection database (`detectable_processed.json`)

A ~10 MB file that maps **executable names** to games: name, aliases, IGDB id, cover, artwork and
the slug of its Backloggd page. When a process matches, the app already knows exactly which game it is
and how to reach its page.

Some executables are generic (`hl2.exe` belongs to several games), so entries may include folder
segments, and the process's actual path is checked against them.

The app **updates this file automatically** at startup, downloading it from this repository with a
conditional check (`ETag`): if it hasn't changed, the download is skipped. It's stored in your
local data folder, and the app always ships an embedded copy as a fallback for the first run or
when you're offline.

If a game isn't in the database, the **window heuristics** kick in: they look for window classes of
known engines (Unreal, Unity, Source, SDL, GLFW, etc) or fullscreen windows, with exclusion lists
for browsers, store launchers, Discord, OBS, IDEs, etc.

On Linux most games run through Proton/Wine, so the game's `.exe` and its path are read from the
Wine process and checked against the same database. Steam games, native ones included, are also
recognized by their Steam App ID, and since X11 windows carry no engine classes, the window heuristics
look at the libraries and files of the process instead (Unity, Unreal, Godot, Ren'Py...).

**Emulators** have their own detectors: they work out which ROM, ISO or title is running (from the
emulator's memory, its window title or its open files, depending on the emulator and the system)
and identify the game from its serial or name.

### 2. Identification via Cloudflare Worker

When detection only yields a **window title** (no game identified), a local fuzzy match is
attempted against the names and aliases in the JSON (normalizing ™®© symbols, version suffixes,
parentheses...). If that fails, [IGDB](https://www.igdb.com/) is queried through a **dedicated
Cloudflare Worker** (`apploggd.nik250dev.workers.dev`).

The Worker acts as a proxy — the app sends it the title, it queries IGDB with its own credentials, and
returns the game's id along with its cover, artwork and URL.

### 3. Reading from and writing to Backloggd

With no API, the only way is to **automate a real browser**. Apploggd uses
[Playwright](https://playwright.dev/) to drive a headless Chromium instance that:

- signs in and obtains your account's cookies,
- opens the game's page, goes to the *Journal* tab, jumps to today, **adds** the session to the time already logged and saves,
- and reads your journal for the "Recently played" list.

## Privacy and data

**Your password is never stored.** It's only used to sign in and then discarded.

Everything Apploggd stores lives in `%LOCALAPPDATA%\Apploggd\` on Windows and in
`~/.local/share/Apploggd/` on Linux:

| File | Contents |
|---|---|
| `settings.json` | Your preferences |
| `user.dat` | Session cookies, **encrypted** (DPAPI on Windows, tied to your user; the system keyring on Linux) |
| `user.name` | Username of the remembered session, so the app can start offline |
| `Keys\` | Encryption keys |
| `detectable_processed.json` | Detection database |
| `blacklist.json` | Apps and games you've blacklisted |
| `pending_sessions.json` | Sessions left in Pending |
| `Logs\` | Daily diagnostic logs (they rotate automatically, 5 files max) |

None of this leaves your machine. The only connections the app makes are to Backloggd (your
account), the IGDB Worker (game titles, to identify them), GitHub (detection database and version
check) and `images.igdb.com` (covers and artwork).

You can wipe all of it from **Settings → Account & Data → Delete data**.

## Troubleshooting

<details>
<summary><b>It says it can't find a browser</b></summary>

Apploggd needs Chromium (or a Chromium-based browser). Install Chrome or Edge, or let the app download its own copy.
</details>

<details>
<summary><b>On Linux, it says the browser is missing system libraries</b></summary>

On a fresh install, the Chromium the app downloads may need libraries the distro doesn't ship by
default (`libnss3`, `libasound2`...). The notice lists them and gives the command that installs them
(it needs `sudo`, so the app doesn't run it on its own). Run it in a terminal and hit **Retry**.
</details>

<details>
<summary><b>It doesn't detect my game</b></summary>

Its executable may not be in the database and its window may not match the heuristics (common with
windowed games running on uncommon engines). If it detects the session but doesn't identify the
game, use the **manual picker** in the confirmation window.

Please [open an issue](https://github.com/nik250dev/apploggd/issues/new) with the game's name and
its executable name (e.g. `MyGame.exe`) so it can be added to the detection database.
</details>

<details>
<summary><b>It detects something that isn't a game</b></summary>

The window heuristics can produce false positives with fullscreen applications. Just hit
**Discard** in the confirmation window: nothing gets logged.

Please [open an issue](https://github.com/nik250dev/apploggd/issues/new) with the name of the
application (and its executable, if you know it) so it can be added to the exclusion list.
</details>

<details>
<summary><b>It asks me to sign in again</b></summary>

Your Backloggd session has expired or was closed elsewhere (password change, logout in the
browser). Just sign in again as usual.
</details>

<details>
<summary><b>It fails to sign in/save the session</b></summary>

This is usually Backloggd's anti-bot protection kicking in, or a change on their site. Try again
after a while; if it persists, check the latest log in the `Logs` folder (see
[Privacy and data](#privacy-and-data)) and open an issue with the error.
</details>

## Known limitations

- **macOS is not supported.** The app may start, but game detection doesn't work there.
- **On Linux, window detection needs X11/XWayland.** Native Wayland windows can't be listed by
  other apps, so a game that isn't in the database or on Steam and runs as a native Wayland window
  won't be detected. Most games (Proton, Unity, SDL 2) use XWayland by default.
- **On Linux, minimizing to the tray needs a tray** (KDE, or GNOME with the AppIndicator extension).
  Without one, the option is hidden and closing the window quits the app.
- Apploggd depends on the structure of the Backloggd website. A redesign of the site can break
  sign-in or the logging of new sessions until an update is released.

## Credits

Created by **[nik250](https://www.reddit.com/user/nik250dev/)**.

Game data from [IGDB](https://www.igdb.com/). This project is not affiliated with or endorsed by
Backloggd or IGDB.
