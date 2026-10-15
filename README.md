# WAI Windows 1.2.0 🔍

WAI searches files in folders you choose using semantic and full-text search. The Windows build runs locally; it contacts the network to download verified model files and, when enabled, check for updates. Search queries and indexed file contents are not sent to a search service.

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

The package identity for this release is WinSearch.WAI. Existing WAI data under %LOCALAPPDATA%\WAI remains in place. Installations with a different package identity are not promised to upgrade automatically; install the new package and verify it before removing an older installation.

Download the [Windows installer](https://github.com/ex-ru/win_search/releases/download/v1.2.0/WinSearch.WAI-win-Setup.exe) or [portable archive](https://github.com/ex-ru/win_search/releases/download/v1.2.0/WinSearch.WAI-win-Portable.zip) from the public [1.2.0 release](https://github.com/ex-ru/win_search/releases/tag/v1.2.0). No GitHub account is required. See [distribution instructions](DISTRIBUTION.md), [third-party distribution status](THIRD_PARTY_NOTICES.md), and the [current publication review](docs/PUBLICATION_REVIEW.md) before broader redistribution.

## Get started

1. Install WAI for your Windows account.
2. Open WAI, choose the folders to search, and let the first-run model download and indexing finish.
3. Press Ctrl+Space, enter a description or exact words, and open a result.

Code indexing, model selection, hardware preferences, and the hotkey can be changed in Settings. They are optional; the default search flow does not require them. See the [user guide](port/WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/docs/User-Guide.md).

## Developer quick start

The repository pins the .NET SDK to 10.0.402 in global.json.

~~~powershell
dotnet test .\port\WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f\windows\WAI.Windows.sln -c Release
~~~

This command works from a source checkout with the pinned SDK. Building a complete application requires additional native runtime and media tools: follow the [builder guide](port/WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/docs/Builder-Guide.md). `Start-WAI.ps1` is a local development launcher and requires an existing `run/` or `run-vulkan/` build; it does not build the app.

For the CLI, point WAI_MODELS to the folder containing downloaded model files:

~~~powershell
$env:WAI_MODELS = Join-Path $env:LOCALAPPDATA 'WAI\Models'
.\run-vulkan\wai.exe status --index .\state\index
.\run-vulkan\wai.exe index 'C:\MyFolder' --index .\state\index
.\run-vulkan\wai.exe search 'договор оказания услуг' --index .\state\index
.\run-vulkan\wai.exe mcp
~~~

See the [MCP guide](docs/MCP_GUIDE.md) for client configuration.

## Validation commands

These commands are available for local validation; this README does not imply that they have been run on your machine.

~~~powershell
dotnet test .\port\WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f\windows\WAI.Windows.sln -c Release

dotnet run --project .\checks\WAI.Performance\WAI.Performance.csproj -c Release -- --counts 10000,100000 --repetitions 12 --report .\state\perf\report.json

.\scripts\check-release.ps1 -PackageDir .\dist\staging -ModelsDir (Join-Path $env:LOCALAPPDATA 'WAI\Models') -Model gemma2
~~~

The release check expects a staged full package and the pinned model files locally. Review the generated reports from each run; results depend on the machine, model, and test corpus. See [the 1.2.0 stabilization report](docs/STABILIZATION.md) for completed checks, measurements and their limits.

Historic search measurements are retained as context, not guarantees. The initial Russian fixture reported Top-1 and Top-5 of 100% across 3 OCR and 3 semantic positive queries, with a 23.2 ms warm response. A later small 13-vector fixture reported 6/6 positive queries, 62.2 ms median and 77.3 ms p95 while another app was active. A separate 8,137-vector run reported about 150 ms median. These small, machine-specific samples do not establish real-world accuracy or latency.

## Build and distribution

Build the standard full package (including FFmpeg) with:

~~~powershell
.\port\WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f\windows\scripts\build-package.ps1 -Version 1.2.0
~~~

The setup executable and portable archive are written under dist\releases. The models are fetched on first use and are not embedded in the package. See [DISTRIBUTION.md](DISTRIBUTION.md) and the [builder guide](port/WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/docs/Builder-Guide.md).

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

See [CONTRIBUTING.md](CONTRIBUTING.md) for setup and bug reports, and [SECURITY.md](SECURITY.md) for reporting vulnerabilities. CI checks source tests, crash recovery and self-contained publishing; the full native installer/update gate remains a separate release check.

## License

WAI project code is distributed under the [MIT License](LICENSE). Bundled components have separate terms; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
