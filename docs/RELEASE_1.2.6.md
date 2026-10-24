# WAI 1.2.6 for Windows

Local file search with a complete, verifiable distribution and a repeatable build process. This release hardens application settings persistence, prevents settings reset during installer upgrades, and adds self-healing from existing SQLite index databases.

## Download and run

Choose **WinSearch.WAI-win-Setup.exe** for a per-user installation or **WinSearch.WAI-win-Portable.zip** for a portable copy. Windows x64; .NET and native runtimes are bundled. Models download separately on first run. Open search with **Ctrl+Space**.

The installer is unsigned: Windows may display an unknown-publisher warning. Check the release source and SHA256SUMS.txt before running it.

## Changes

- **Settings persistence & lock-free retry reading:**
  - Resolved file sharing lock conflicts (`IOException`) when updating via Velopack installer or restarting the application.
  - Implemented shared-read mode (`FileShare.ReadWrite`) with 5 retries and exponential backoff, preventing transient file access errors from triggering fallback to clean settings.
- **Automatic configuration backup (`settings.json.bak`):**
  - Settings are mirrored to `settings.json.bak` on every successful save.
  - If the primary configuration file is missing, empty, or corrupted, settings transparently restore from the backup.
- **Self-healing from existing SQLite index:**
  - When configuration files are missing or search folders are empty, WAI automatically inspects the local index database (`index.sqlite`).
  - Deduces and restores user search folders from indexed file paths (standard folders like `Documents`, `Downloads`, as well as cloud/external drives like `G:\Мой диск`).
  - Marks setup as completed (`SetupDone = true`), preventing inappropriate display of the initial onboarding wizard.
- **Auto-save on Settings window close:**
  - Added immediate persistence when adding or removing folders in the Settings dialog.
  - Added `OnClosing` handler to automatically apply and save user changes when closing the Settings window via the title bar close button (`X`) or Alt+F4.

WAI project code uses MIT. Individual bundled components retain their own licenses; FFmpeg is LGPL-2.1-or-later in this build, dav1d is BSD-2-Clause and zlib uses the zlib license. Matching media source: [ffmpeg-corresponding-source.zip](https://github.com/ex-ru/win_search/releases/download/v1.2.1/ffmpeg-corresponding-source.zip).

## Updates and validation

The package identity remains **WinSearch.WAI**. Existing WAI settings and indexes under `%LocalAppData%\WAI` are retained.
