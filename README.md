# Local Achievements (Steam Tools Tracker)

A generic Playnite plugin designed to synchronize local Steam achievements and inject them seamlessly into the SuccessStory plugin database. This tool is built specifically for environments utilizing local `.lua` and `.bin` files (**Steam Tools**, **Lua Tools**, and **GreenLuma** setups using `stplug-in`).

## Why is this plugin not on the official Playnite Add-on Store?

A submission to the official Playnite Add-on Database ([`JosefNemec/PlayniteAddonDatabase#539`](https://github.com/JosefNemec/PlayniteAddonDatabase/pull/539)) was closed by Playnite's creator (Josef Nemec) with the following note:

> *"This is going to be a no go for the addon DB since it's integration for a tool designed for piracy."*

Because of that, **Local Achievements** is distributed exclusively through this GitHub repository and includes a built-in auto-updater that checks GitHub Releases directly.

## Features

* **Offline Synchronization:** Reads local Steam binary files and schemas to recover accurate achievement unlock dates.
* **Automatic Tagging:** Scans your library and automatically applies a "Steam Tools" tag to supported games.
* **SuccessStory Integration:** Temporarily hides supported games from SuccessStory during gameplay to prevent data overwriting, silently injecting the correct data once the game is closed.
* **Playtime Sync:** Imports the hours played from Steam (`localconfig.vdf`) into Playnite for Steam Tools / Lua Tools games. If Steam has more playtime than Playnite, Playnite is updated to the Steam value; if Playnite has equal or more, nothing changes.
* **Automatic Restore:** If SuccessStory refreshes a Steam Tools game and drops its unlocked achievements, the plugin restores them within a few seconds and offers to restart Playnite right on screen (showing the game and achievement count) and via notification. It can be turned off in the plugin settings.
* **Automatic Updates:** Checks GitHub Releases when Playnite starts and, when a new version exists, shows the installed and new version numbers plus what changed. One click downloads the release, verifies its SHA-256 and installs it, with automatic rollback if anything fails. Choose a window, a Playnite notification only, or no checks in the plugin settings.
* **Localization:** Native support for English (en-US), Portuguese (pt-BR), and Spanish (es-ES).

## Installation

1. Navigate to the Releases page of this repository.
2. Download the latest `.pext` extension file.
3. Drag and drop the `.pext` file into your open Playnite window, or install it manually via the Playnite menu: Add-ons > Install extension file...

## Usage

* Right-click any supported game and open the **Local Achievements** submenu: **Sync Achievements** or **Sync Playtime**.
* The plugin will automatically scan for new games and sync data in the background upon application startup or library updates.