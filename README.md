<div align="center">

<img src="app/BackloggdMirror/Assets/app-logo.png" alt="Apploggd" width="96" />

# Apploggd

**A game scrobbler for [Backloggd](https://backloggd.com).**

Tired of manually logging every play session on Backloggd? Me too :)

Apploggd automatically detects what you're playing, times the session and logs the playtime
to your Backloggd journal when you close the game.

![Version](https://img.shields.io/badge/version-1.0.0-8b5cf6)
![Platform](https://img.shields.io/badge/platform-Windows-0078d4)
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
  heuristics (known game engines, fullscreen windows, and so on).
- **Manual picker** — if a game isn't recognized, you can search Backloggd from within the app and
  pick the right one.
- **Playtime accumulates per day** — if you play several sessions of the same game on the same
  day, they're added to the existing entry instead of overwriting it.
- **"Recently played" list** with your latest journal entries.
- **Self-updating detection database**

## Requirements

| | |
|---|---|
| **Operating system** | Windows 10/11. *(Linux/macOS in development, not supported yet)* |
| **Browser** | A Chromium-based one (Chrome, Edge). If you don't have one, the app will offer to download its own (~400 MB). |
| **Account** | A [Backloggd](https://backloggd.com) account. |

## Installation

1. Download the latest version from the 
   [releases page](https://github.com/nik250dev/apploggd/releases).
2. Extract it wherever you like (no installer; it's portable).
3. Run `Apploggd.exe`.

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

If a game isn't in the database, the **window heuristics** kick in: they looks for window classes of
known engines (Unreal, Unity, Source, SDL, GLFW, etc) or fullscreen windows, with exclusion lists
for browsers, store launchers, Discord, OBS, IDEs, etc.

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

Everything Apploggd stores lives in `%LOCALAPPDATA%\Apploggd\`:

| File | Contents |
|---|---|
| `settings.json` | Your preferences |
| `user.dat` | Session cookies, **encrypted** (DPAPI, tied to your Windows user) |
| `Keys\` | Encryption keys |
| `detectable_processed.json` | Detection database |
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
after a while; if it persists, check the latest log in `%LOCALAPPDATA%\Apploggd\Logs\` and open an
issue with the error.
</details>

## Known limitations

- **Game detection only works on Windows.** On Linux and macOS the app starts and the interface
  works, but detection isn't implemented yet.
- Apploggd depends on the structure of the Backloggd website. A redesign of the site can break
  sign-in or the logging of new sessions until an update is released.

## Credits

Created by **[nik250](https://www.reddit.com/user/nik250dev/)**.

Game data from [IGDB](https://www.igdb.com/). This project is not affiliated with or endorsed by
Backloggd or IGDB.
