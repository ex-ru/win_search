# WAI 1.2.2 for Windows

Local file search with a complete, verifiable distribution and a repeatable build process. This release includes UI styling improvements for dark and light themes, unified expander styling with an accessible vector icon, autostart registry fixes, and restored match relevance percentage indicators.

## Download and run

Choose **WinSearch.WAI-win-Setup.exe** for a per-user installation or **WinSearch.WAI-win-Portable.zip** for a portable copy. Windows x64; .NET and native runtimes are bundled. Models download separately on first run. Open search with **Ctrl+Space**.

The installer is unsigned: Windows may display an unknown-publisher warning. Check the release source and SHA256SUMS.txt before running it.

## Changes

- **Settings window (Expander & Themes):** The "Дополнительно" section now uses a custom modern card styling with crisp theme-aware text, responsive hover effects, and a vector gear icon instead of unstyled dark text.
- **Start with Windows:** Fixed registry persistence for autostart (`Settings.StartWithWindows`), cleaned up legacy `DigUp` keys, and added `--silent` flag for tray startup.
- **Dynamic theme brushes:** Improved contrast and theming across search chips, header buttons, scrollbars, and the search clear button.
- **Search relevance:** Restored the percentage relevance indicator under the file type badge in search results.
- **Diagnostics:** Sanitized developer and machine paths in validation and diagnostic reports.

WAI project code uses MIT. Individual bundled components retain their own licenses; FFmpeg is LGPL-2.1-or-later in this build, dav1d is BSD-2-Clause and zlib uses the zlib license. Matching media source: [ffmpeg-corresponding-source.zip](https://github.com/ex-ru/win_search/releases/download/v1.2.1/ffmpeg-corresponding-source.zip).

## Updates and validation

The package identity remains **WinSearch.WAI**. Existing WAI settings and indexes under `%LocalAppData%\WAI` are retained.
