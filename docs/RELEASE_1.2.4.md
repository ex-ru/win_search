# WAI 1.2.4 for Windows

Local file search with a complete, verifiable distribution and a repeatable build process. This release introduces a thumbnail gallery grid view for image and media search results, seamless segmented view mode switching, asynchronous thumbnail caching, synchronized selection, and 2D keyboard navigation.

## Download and run

Choose **WinSearch.WAI-win-Setup.exe** for a per-user installation or **WinSearch.WAI-win-Portable.zip** for a portable copy. Windows x64; .NET and native runtimes are bundled. Models download separately on first run. Open search with **Ctrl+Space**.

The installer is unsigned: Windows may display an unknown-publisher warning. Check the release source and SHA256SUMS.txt before running it.

## Changes

- **Thumbnail gallery view (GAL-01):** Added an alternative thumbnail gallery mode (⊞) alongside the classic list view (☰). Results are displayed in an adaptive grid of rich media cards with thumbnail previews, relevance score badges, and file type indicators.
- **Asynchronous thumbnail loader & cache:** Thumbnails are decoded and scaled off the UI thread via the local image cache (`%LocalAppData%\WAI\thumbs\`), preventing UI freezes during fast scrolling.
- **Segmented view switcher:** A clean modern view mode toggle button is integrated directly into the filter bar row, remembering user view preference in `Settings.PreferGallery`.
- **2D Keyboard navigation & Quick Look:** Full arrow-key navigation across the gallery grid (`Left` / `Right` for adjacent items, `Up` / `Down` for full row jumps), `Space` for instant Quick Look preview, and `Enter` / double click to open the file.
- **Unified theme styling:** Gallery cards feature crisp borders, hover glow, and active selection highlights tailored for both dark and light modes.

WAI project code uses MIT. Individual bundled components retain their own licenses; FFmpeg is LGPL-2.1-or-later in this build, dav1d is BSD-2-Clause and zlib uses the zlib license. Matching media source: [ffmpeg-corresponding-source.zip](https://github.com/ex-ru/win_search/releases/download/v1.2.1/ffmpeg-corresponding-source.zip).

## Updates and validation

The package identity remains **WinSearch.WAI**. Existing WAI settings and indexes under `%LocalAppData%\WAI` are retained.
