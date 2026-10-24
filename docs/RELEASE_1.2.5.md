# WAI 1.2.5 for Windows

Local file search with a complete, verifiable distribution and a repeatable build process. This release fixes system tray context menu responsiveness and resolves Windows 10/11 autostart issues.

## Download and run

Choose **WinSearch.WAI-win-Setup.exe** for a per-user installation or **WinSearch.WAI-win-Portable.zip** for a portable copy. Windows x64; .NET and native runtimes are bundled. Models download separately on first run. Open search with **Ctrl+Space**.

The installer is unsigned: Windows may display an unknown-publisher warning. Check the release source and SHA256SUMS.txt before running it.

## Changes

- **Instant tray menu and settings response:** Eliminated context menu and dialog lag. Tray index progress is now read instantly from an asynchronously refreshed memory cache without blocking SQLite segment scans. Model status checks in Settings use instantaneous size and existence verification rather than synchronous multi-gigabyte SHA-256 hash passes on the UI thread.
- **Background settings statistics:** Folder vector and database size calculations in the Settings dialog now run asynchronously in background tasks, keeping the UI completely fluid and responsive.
- **Reliable Windows autostart & Task Manager synchronization:**
  - Resolved an issue where autostart on Windows boot did not trigger despite being enabled in settings.
  - Added full synchronization with `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run`, clearing disabled flags (`0x03`) left by Windows Task Manager or Settings and setting explicit enabled status (`0x02`).
  - Added self-healing check (`Settings.SyncStartupApproved()`) on application launch.
  - Set working directory to the application base folder on startup (`Directory.SetCurrentDirectory(AppContext.BaseDirectory)`), preventing execution failures when Windows launches the app with working directory set to `System32`.
  - Cleaned up obsolete legacy registry entries.

WAI project code uses MIT. Individual bundled components retain their own licenses; FFmpeg is LGPL-2.1-or-later in this build, dav1d is BSD-2-Clause and zlib uses the zlib license. Matching media source: [ffmpeg-corresponding-source.zip](https://github.com/ex-ru/win_search/releases/download/v1.2.1/ffmpeg-corresponding-source.zip).

## Updates and validation

The package identity remains **WinSearch.WAI**. Existing WAI settings and indexes under `%LocalAppData%\WAI` are retained.
