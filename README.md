# WAI Windows 1.2.4 🔍

WAI searches files in folders you choose using semantic and full-text search. The Windows build runs locally; it contacts the network to download verified model files and, when enabled, check for updates. Search queries and indexed file contents are not sent to a search service.

[![Release: 1.2.4](https://img.shields.io/badge/Release-1.2.4-brightgreen.svg)](https://github.com/ex-ru/win_search/releases/tag/v1.2.4)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue.svg)](https://microsoft.com)
[![Runtime: .NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com)
[![Hardware: Vulkan / CPU](https://img.shields.io/badge/Hardware-Vulkan%20%7C%20CPU-success.svg)](https://github.com/ggerganov/llama.cpp)

## What it does

- Combines semantic retrieval with SQLite FTS5 full-text search. FTS5 Porter stemming is not a general Russian morphology engine; results depend on the query, content, and selected embedding model.
- Indexes supported documents, images, screenshots, audio, and video from folders you select. Exact format and codec support depends on the installed Windows components and the file.
- Includes Windows Runtime OCR for supported Windows OCR languages.
- Uses CPU or Vulkan inference in the shipped Windows package. CUDA is not included.
- The standard full package includes FFmpeg. Model files are downloaded separately and checked against pinned size and SHA-256 values before use.
- Stores WAI settings and index data under %LOCALAPPDATA%\WAI.
- **Thumbnail gallery view (1.2.4):** Offers an alternative media grid view (⊞) alongside the classic list view (☰), with asynchronous background thumbnail decoding and disk caching (`%LOCALAPPDATA%\WAI\thumbs`) and 2D keyboard navigation.
- **Code editor deep linking (1.2.3):** Opening code search results navigates directly to the matched line and column in VS Code / Cursor, JetBrains IDEs, Visual Studio, Notepad++, Sublime Text, and Kate.
- **PDF page navigation (1.2.3):** Opening PDF documents jumps directly to the matched page in Adobe Acrobat, Foxit Reader, SumatraPDF, PDF-XChange, Microsoft Edge, and Google Chrome.
- **Media timestamp seeking (1.2.3):** Video and audio search results open directly at the matched timestamp in VLC, MPC-HC/BE, MPV, and PotPlayer; Quick Look preview includes an interactive seek timeline.
- **Battery saver throttling & adaptive ETA (1.2.3):** Background indexing automatically pauses or throttles during Windows Battery Saver or low charge (≤20%); real-time ETA dynamically calibrates against live worker throughput.
- **Hardware agreement & GPU auto-tuning (1.2.3):** Cosine agreement checks (`threshold: 0.999`) ensure GPU embeddings match CPU calculations, falling back automatically if integrated graphics produce artifacts.

The package identity for this release is WinSearch.WAI. Existing WAI data under %LOCALAPPDATA%\WAI remains in place. Installations with a different package identity are not promised to upgrade automatically; install the new package and verify it before removing an older installation.

Download the [Windows installer](https://github.com/ex-ru/win_search/releases/download/v1.2.4/WinSearch.WAI-win-Setup.exe) or [portable archive](https://github.com/ex-ru/win_search/releases/download/v1.2.4/WinSearch.WAI-win-Portable.zip) from the public [1.2.4 release](https://github.com/ex-ru/win_search/releases/tag/v1.2.4) (or the [latest release](https://github.com/ex-ru/win_search/releases/latest)). No GitHub account is required. See [1.2.4 release notes](docs/RELEASE_1.2.4.md), [distribution instructions](DISTRIBUTION.md), [third-party notices](THIRD_PARTY_NOTICES.md), and [1.2.1 verification](docs/RELEASE_VERIFICATION_1.2.1.md). Clean Windows 11 feedback is being collected by the owner’s volunteer group.

## Get started

1. Install WAI for your Windows account.
2. Open WAI, choose the folders to search, and let the first-run model download and indexing finish.
3. Press Ctrl+Space, enter a description or exact words, and open a result.
- Press **Space** for instant Quick Look preview of images, PDF pages, or audio/video with an interactive timeline.
- Switch between classic list view (☰) and thumbnail gallery (⊞) via the segmented view button.
- Code results open directly at the matched line and column in your editor; PDF results open directly to the matched page.

Code indexing, model selection, hardware preferences, and the hotkey can be changed in Settings. They are optional; the default search flow does not require them. See the [user guide](port/WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/docs/User-Guide.md).

## Developer quick start

The repository pins the .NET SDK to 10.0.402 in global.json. Install that exact SDK for source builds.

~~~powershell
dotnet test .\port\WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f\windows\WAI.Windows.sln -c Release
~~~

This command works from a source checkout with the pinned SDK. Build the full installer and portable package with `.\scripts\build-release.ps1`; pinned native dependencies are downloaded and verified automatically. Follow the [builder guide](port/WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/docs/Builder-Guide.md). `Start-WAI.ps1` is a local development launcher and requires an existing `run/` or `run-vulkan/` build; it does not build the app.

For the CLI, point WAI_MODELS to the folder containing downloaded model files:

~~~powershell
$env:WAI_MODELS = Join-Path $env:LOCALAPPDATA 'WAI\Models'
.\dist\1.2.4\staging\wai.exe status --index .\state\index
.\dist\1.2.4\staging\wai.exe index 'C:\MyFolder' --index .\state\index
.\dist\1.2.4\staging\wai.exe search 'договор оказания услуг' --index .\state\index
.\dist\1.2.4\staging\wai.exe mcp
~~~

See the [MCP guide](docs/MCP_GUIDE.md) for client configuration.

## Validation commands

These commands are available for local validation; this README does not imply that they have been run on your machine.

~~~powershell
dotnet test .\port\WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f\windows\WAI.Windows.sln -c Release

dotnet run --project .\checks\WAI.Performance\WAI.Performance.csproj -c Release -- --counts 10000,100000 --repetitions 12 --report .\state\perf\report.json

.\scripts\check-release.ps1 -PackageDir .\dist\1.2.4\staging -ModelsDir (Join-Path $env:LOCALAPPDATA 'WAI\Models') -Model gemma2 -SkipEvaluation -Native
~~~

The release check expects a staged full package and the pinned model files locally. Review the generated reports from each run; results depend on the machine, model, and test corpus. See [1.2.4 release notes](docs/RELEASE_1.2.4.md), [1.2.1 release verification](docs/RELEASE_VERIFICATION_1.2.1.md) and [historical 1.2.0 stabilization](docs/STABILIZATION.md) for checks and their limits.

Historic search measurements are retained as context, not guarantees. The initial Russian fixture reported Top-1 and Top-5 of 100% across 3 OCR and 3 semantic positive queries, with a 23.2 ms warm response. A later small 13-vector fixture reported 6/6 positive queries, 62.2 ms median and 77.3 ms p95 while another app was active. A separate 8,137-vector run reported about 150 ms median. These small, machine-specific samples do not establish real-world accuracy or latency.

## Build and distribution

Build the standard full package (including FFmpeg) with:

~~~powershell
.\scripts\build-release.ps1
~~~

The setup executable and portable archive are written under dist\1.2.4\releases. The models are fetched on first use and are not embedded in the package. See [DISTRIBUTION.md](DISTRIBUTION.md) and the [builder guide](port/WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/docs/Builder-Guide.md).

## Project history

This repository contains a Windows port of [ARahim3/WAI](https://github.com/ARahim3/WAI), originally developed for macOS under the MIT license. The Windows implementation is maintained in this repository.

## Repository map and contributions

| Path | Purpose |
|---|---|
| `port/WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/` | Active Windows application, core, worker, CLI and unit tests |
| `upstream/` and the rest of `port/` | Historical macOS snapshots and porting references |
| `checks/`, `scripts/`, `.github/` | Validation tools, release scripts and Windows CI |
| `docs/`, `BACKLOG.md` | Guides, measured results and prioritized remaining work |
| `results/`, `testbed*` | Selected historical evidence and synthetic test fixtures |
| `dist/`, `tools/`, `models/`, `state/` | Mostly ignored local packages, dependencies, models and state |

See [CONTRIBUTING.md](CONTRIBUTING.md) for setup and bug reports, and [SECURITY.md](SECURITY.md) for reporting vulnerabilities. CI checks source tests, crash recovery, self-contained publishing and full native packaging. Local model fixtures and portable upgrade are checked before publication. See [Windows 11 testing](docs/TESTING_WINDOWS_11_RU.md).

## License

WAI project code is distributed under the [MIT License](LICENSE). Bundled components have separate terms; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
