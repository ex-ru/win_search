# WAI 1.2.1 for Windows

Local file search with a complete, verifiable distribution and a repeatable build process. This release includes the background efficiency improvements merged after 1.2.0.

## Download and run

Choose **WinSearch.WAI-win-Setup.exe** for a per-user installation or **WinSearch.WAI-win-Portable.zip** for a portable copy. Windows x64; .NET and native runtimes are bundled. Models download separately on first run. Open search with **Ctrl+Space**.

The installer is unsigned: Windows may display an unknown-publisher warning. Check the release source and SHA256SUMS.txt before running it.

## Changes

- Background indexing uses bounded CPU threads, lower priority and Windows efficiency hints. Query inference keeps its own foreground configuration.
- Index cancellation and incremental reconciliation remain active. Renamed/deleted folders with temporary-looking names are still reconciled.
- All shipped native inputs have pinned versions, source URLs and SHA-256 metadata in native-provenance.json.
- Required Microsoft Visual C++ runtime DLLs are bundled beside the executables. Native checks audit PE imports and verify that llama/MTMD load the packaged runtime rather than a machine-global copy.
- PDFium is updated to 157.0.8100.0 and ships its distribution and component notices.
- FFmpeg/FFprobe are built from FFmpeg 9.0.2 without GPL/nonfree components. BSD dav1d retains CPU AV1 decoding; zlib supports PNG and compressed media structures. Network protocols and optional external codec libraries are omitted from this local-file build.
- licenses/ contains WAI, llama/LLVM, PDFium, media, managed dependencies, .NET runtime and Velopack runtime notices. Missing required materials stop packaging.
- The same release provides **ffmpeg-corresponding-source.zip**, containing the exact media source archives and build recipe.
- Managed dependencies use lock files; the SDK and Velopack tool versions are pinned. Standard packaging prepares CPU/Vulkan/media inputs from verified archives instead of a developer's prebuilt application folder.

WAI project code uses MIT. Individual bundled components retain their own licenses; FFmpeg is LGPL-2.1-or-later in this build, dav1d is BSD-2-Clause and zlib uses the zlib license. Matching media source: [ffmpeg-corresponding-source.zip](https://github.com/ex-ru/win_search/releases/download/v1.2.1/ffmpeg-corresponding-source.zip).

## Updates and validation

The package identity remains **WinSearch.WAI**. Existing WAI settings and indexes under `%LocalAppData%\WAI` are retained. Installations under an older, different package identity require the WAI installer.

Release validation covers CPU workers, pinned local models, PDF/media decoding, the synthetic 240-query search corpus, SQLite recovery, corrupt-package rejection and real portable update application. Results and limits are recorded in [release verification](https://github.com/ex-ru/win_search/blob/main/docs/RELEASE_VERIFICATION_1.2.1.md). These checks do not establish accuracy on every collection or compatibility with every GPU/driver.

Clean Windows 11 installation and GUI feedback are being tested by the owner's volunteer group. That manual test has not been marked as passed by the automated release checks. The app keeps CPU mode available; Windows 11 efficiency hints may be unavailable on older Windows builds.
