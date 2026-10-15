# WAI 1.2.0 for Windows

This release stabilizes local search and simplifies the first-run experience.

## Changes

- Serialized indexing, safe cancellation and shutdown, atomic SQLite transactions, and recovery after interrupted writes.
- Folder and file-type filters now apply before result limits. Search keeps current metadata when an index changes in the background.
- New requests cancel obsolete work. Text results appear first, followed by semantic results; worker startup and requests have bounded timeouts.
- Debounced folder monitoring and incremental reconciliation avoid unnecessary full rescans. Temporarily inaccessible files are retained.
- Russian word-form matching and exact identifiers have regression coverage. Semantic search uses the dimension and fingerprint of its own index.
- Large vector scans reuse buffers, batch metadata reads, and use bounded parallelism. ANN remains outside the active search path.
- Model downloads check pinned revisions, size and SHA-256. First-run readiness includes starting the selected model.
- A three-step setup and a shorter Settings screen keep the basic workflow simple. Technical controls are under Advanced; the gallery and cloud reranker remain deferred.
- Active Windows UI, package identity and documentation use WAI. The package includes a self-contained .NET 10 runtime, CPU/Vulkan libraries, PDFium and FFmpeg.

## Installation

Use **WinSearch.WAI-win-Setup.exe** for a per-user installation or **WinSearch.WAI-win-Portable.zip** for a portable copy. Models download separately. Press **Ctrl+Space** to open search.

The package ID is **WinSearch.WAI**. Existing WAI data under `%LocalAppData%\WAI` is preserved. Installations with a different package ID require the new installer; automatic upgrades between identities are not guaranteed.

Checksums are in **SHA256SUMS.txt**. Release verification and its limits are recorded in [docs/STABILIZATION.md](https://github.com/ex-ru/win_search/blob/v1.2.0/docs/STABILIZATION.md).

## Limits

This repository is private: download the assets in a signed-in browser with repository access. The application's anonymous GitHub update source cannot read a private repository. For internal automatic updates, use the supported local/network release folder in Advanced settings.

The installers are not code-signed. Windows may show a publisher warning. CPU inference is tested locally; this release does not claim a full GPU and driver compatibility matrix. Synthetic search and performance fixtures do not establish accuracy or latency on every user's collection. Russian matching covers regular word forms and selected alternations, rather than a complete dictionary. A single indexed semantic fragment has no relative confidence scale, so exact words remain necessary in that case.
