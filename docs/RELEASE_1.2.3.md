# WAI 1.2.3 for Windows

Local file search with a complete, verifiable distribution and a repeatable build process. This release introduces precise deep linking to line and column numbers across all major code editors, exact page navigation for PDF viewers and browsers, media timestamp seeking with an interactive preview timeline, adaptive heterogeneous indexer ETA calibration, automated battery saver throttling, and hardware agreement checks with GPU auto-tuning.

## Download and run

Choose **WinSearch.WAI-win-Setup.exe** for a per-user installation or **WinSearch.WAI-win-Portable.zip** for a portable copy. Windows x64; .NET and native runtimes are bundled. Models download separately on first run. Open search with **Ctrl+Space**.

The installer is unsigned: Windows may display an unknown-publisher warning. Check the release source and SHA256SUMS.txt before running it.

## Changes

- **Code editor deep linking (IDE-01):** Opening code search results now navigates directly to the matched line and column in VS Code / Cursor (`--goto file:line:col`), JetBrains IDEs (`--line L --column C file`), Notepad++ (`-nL -cC`), Sublime Text (`file:line:col`), Visual Studio (`/command "edit.goto L"`), and Kate (`-l L -c C`).
- **PDF page navigation (PDF-01):** Matched PDF documents open directly to the target page in Adobe Acrobat (`/A "page=N"`), Foxit Reader, SumatraPDF (`-page N`), PDF-XChange Editor, Microsoft Edge, and Google Chrome (`file#page=N`).
- **Media deep seeking & timeline (MED-01):** Matched video and audio files open directly at the matched timestamp in VLC (`--start-time=S`), MPC-HC / MPC-BE (`/start MS`), MPV (`--start=S`), and PotPlayer (`/seek=S`). The media preview window now includes an interactive seek slider and Left/Right keyboard shortcuts for smooth timeline scrub and playback control.
- **Heterogeneous pace calibration & ETA (ETA-01):** Indexing status now computes real-time estimated time of arrival (ETA) scaled by media type cost weights (plain text vs. dense PDF OCR vs. audio transcription) and continuously calibrated against live worker processing pace.
- **Battery saver policy (PWR-01):** The background indexer automatically throttles and pauses when Windows activates Battery Saver or when the system is on battery power below 20% charge, resuming transparently once plugged in or recharged.
- **Hardware agreement & GPU auto-tuning (OPT-05):** Added runtime cosine similarity validation between GPU and CPU embedding vectors (`threshold: 0.999`) and automated benchmark fallbacks for integrated Intel graphics to prevent GPU divergence or thermal throttling.

WAI project code uses MIT. Individual bundled components retain their own licenses; FFmpeg is LGPL-2.1-or-later in this build, dav1d is BSD-2-Clause and zlib uses the zlib license. Matching media source: [ffmpeg-corresponding-source.zip](https://github.com/ex-ru/win_search/releases/download/v1.2.1/ffmpeg-corresponding-source.zip).

## Updates and validation

The package identity remains **WinSearch.WAI**. Existing WAI settings and indexes under `%LocalAppData%\WAI` are retained.
