# WAI for Windows 1.2.1 — Builder Guide

## Requirements and pinned inputs

- Windows x64, Git, PowerShell 5.1 or 7, and the exact .NET SDK **10.0.402**. `global.json` disables SDK roll-forward.
- The five project `packages.lock.json` files are restored with `RestoreLockedMode=true`. The package builder installs the pinned Velopack CLI 1.2.161 when needed.
- `native-dependencies.json` is the source of truth for native artifact URLs, versions, sizes, and SHA-256: llama.cpp b11461, PDFium 157.0.8100.0, FFmpeg 9.0.2, dav1d 1.5.4, and zlib 1.3.2. `scripts/prepare-runtime.ps1` verifies archive hashes before extracting the runtime.
- The standard package needs no Python, CUDA, or Visual Studio C++ toolchain. Rebuilding the FFmpeg bundle from source is a separate Linux CI job using `scripts/build-ffmpeg.sh` and the compiler/tool versions recorded in that bundle.

The release package contains CPU and Vulkan backends, PDFium and FFmpeg. CUDA is not included. Models are downloaded separately and validated against pinned size/SHA-256 metadata. Packaged users do not need to install .NET, Python, or FFmpeg.

App-local Microsoft Visual C++ CRT files are also pinned and prepared with built-in Windows extraction tools. They are proprietary redistributable components subject to the applicable Visual Studio distributable-code terms; original terms and references are in `licenses/vcruntime/`.

## Clean checkout build

Run from the repository root. Confirm that the SDK selected by `global.json` is 10.0.402, then run the locked regression suite and the release wrapper:

~~~powershell
dotnet --version
$win = '.\port\WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f\windows'
dotnet test "$win\WAI.Windows.sln" -c Release -p:RestoreLockedMode=true --logger trx
.\scripts\build-release.ps1
~~~

`release-version.json` selects the package version; for this candidate the output is `dist\1.2.1\staging` and `dist\1.2.1\releases`. The builder creates a full package and rejects the removed Slim profile. It publishes the GUI, CLI, and worker self-contained for `win-x64`, copies the verified native runtime and its notices, installs Velopack 1.2.161 if needed, creates installer/portable assets, and writes `SHA256SUMS.txt`.

The first 1.2.1 CI package run seeds `tools\native-cache` with the `ffmpeg-win-x64` Actions artifact from the run ID in `release-version.json`; the artifact must contain `ffmpeg-tools-win-x64.zip` and `ffmpeg-corresponding-source.zip`. For a local build before 1.2.1 is public, download that same run's artifact and place those two files in `tools\native-cache`. Check their sizes and SHA-256 against `native-dependencies.json`. The other native archives are downloaded and verified by `prepare-runtime.ps1`. After publication, the pinned FFmpeg archive URLs also resolve from the public release.

The native lock file pins the original FFmpeg, dav1d, and zlib source archives as well as the Windows runtime archives. The release carries the matching source bundle, `native-provenance.json`, and legal notices for native, managed, .NET runtime, and Velopack dependencies. Their source files live under `licenses/`; `THIRD_PARTY_NOTICES.md` summarizes the shipped components. Missing notices or changed native inputs stop packaging. A clean Windows checkout can repeat the build from these pinned inputs; byte-for-byte equality of Velopack output across machines/runs is not claimed.

## CI and final release gates

The candidate workflow `.github/workflows/package.yml` runs locked tests, builds the full package, checks the native files and notices, then uploads release assets to a **draft**. A draft is not a public release, and the workflow does not replace the final model and update checks.

Before publication, `scripts/check-release.ps1` must validate the actual staged package with real model files and native checks enabled. `scripts/check-update-apply.ps1` must apply the new package to a disposable copy of the original 1.2.0 portable release and verify retained state and search. These final package/model/update gates are pending for the current candidate; see `docs/RELEASE_VERIFICATION_1.2.1.md` for the recorded status. Clean Windows 11 GUI installation is a separate external test with the owner's volunteers and must remain pending until they report it.

After all automated gates pass, `scripts/publish-release.ps1` verifies all required assets against `SHA256SUMS.txt` and GitHub's reported sizes/digests before publishing. Pass the full SHA of the tested source commit as `TargetCommitish`; published tags and release assets are immutable.

## Data migration and runtime limits

The package ID is `WinSearch.WAI`. Existing WAI data under `%LocalAppData%\WAI` remains in place; automatic in-place upgrade from an older package ID is not guaranteed. Confirm required data with the new installation before removing an older identity.

Vulkan availability and speed depend on the GPU and installed driver. CPU remains available in settings. Historical performance and accuracy results describe small fixtures and specific hardware; they do not predict results on every user's files or device.
