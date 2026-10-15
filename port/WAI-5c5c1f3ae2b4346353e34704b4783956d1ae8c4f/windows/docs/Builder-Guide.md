# WAI for Windows 1.2.0 — Builder Guide

## Requirements

- Windows 10 or 11, x64, with Git.
- .NET SDK 10.0.402. The repository pins this SDK in global.json; verify the selected version with dotnet --version.
- Python 3.12, CMake, Ninja, and Visual Studio C++ build tools are needed for rebuilding the native llama.cpp runtime. They are not required to run the packaged app.

The shipped package uses CPU and Vulkan backends and includes FFmpeg by default. CUDA is not part of the release package. Model files are downloaded when needed and checked against pinned source, file size, and SHA-256 metadata. Users do not need a separate .NET, Python, or FFmpeg installation.

## Build

Run commands from the repository root. Managed source checks do not require a local `run-vulkan/` application build:

~~~powershell
$win = '.\port\WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f\windows'
dotnet build "$win\WAI.Windows.sln" -c Release
dotnet test "$win\WAI.Windows.sln" -c Release
~~~

A fresh clone is **not yet sufficient to build the standard distribution**. `build-package.ps1` requires a prepared `run-vulkan/` native payload (`llama.dll`, `mtmd.dll`, `ggml*.dll`, `libomp.dll`), `tools/llama-vulkan/LICENSE-LLVM-OpenMP`, and `tools/ffmpeg/{ffmpeg,ffprobe}.exe`. PDFium is currently tracked under `windows/native/x64/`. These external binaries are not downloaded by the packaging command. See `PUB-02` and `PUB-04` in the root backlog for matching notices and reproducible native dependency preparation.

The existing `build-llama-windows.ps1` is an experimental source build, **not** a bootstrap for the standard CPU/Vulkan package. It currently enables CUDA, needs the CUDA toolchain and Visual Studio 2022 C++ tools, writes to `windows/native/`, and uses PowerShell 7 syntax. Do not invoke it through Windows PowerShell 5.1 or assume it fills `run-vulkan/`.

Build the standard full package with the default profile:

~~~powershell
& "$win\scripts\build-package.ps1" -Version 1.2.0
~~~

The package identity is WinSearch.WAI. The build writes its staged payload to dist\staging and release artifacts to dist\releases. The full package includes FFmpeg. Model files are not embedded in the installer.

## Model integrity

Model URLs and expected size/SHA-256 values are pinned in WAI.Core/ModelCatalog.cs and WAI.Core/ModelFiles.cs. The downloader validates the file before it is used. Keep model metadata changes explicit and review the upstream revision and checksums when updating a model.

## Release checks

The following commands are available to run before a release. Their presence here does not mean they have passed in the current checkout; retain and review reports from actual runs.

~~~powershell
$models = Join-Path $env:LOCALAPPDATA 'WAI\Models'
.\scripts\check-release.ps1 -PackageDir .\dist\staging -ModelsDir $models -Model gemma2

dotnet run --project .\checks\WAI.Performance\WAI.Performance.csproj -c Release -- --counts 10000,100000 --repetitions 12 --report .\state\perf\report.json
~~~

The release check expects a staged full package and the locally available model files. The performance tool reports measurements for the requested corpus and this machine; it is not a universal latency requirement. Add --million only when there is enough disk space for the generated test index.

## Distribution and data migration

Version 1.2.0 uses package identity WinSearch.WAI. Existing WAI data in %LOCALAPPDATA%\WAI remains in place. The new package identity differs from older installers, so automatic in-place upgrade across package identities is not guaranteed. Test the new installation and confirm that needed data is available before uninstalling an older identity.

The package contains CPU and Vulkan runtimes. Vulkan behavior depends on hardware and driver; test on the target systems. The shipped package does not contain CUDA.

## Historical measurements

Older evaluation results remain in README.md and CHANGELOG.md as historical context. The Russian quality set and mixed media set were small fixtures; their percentages and timings do not establish expected performance or accuracy for real user collections.
