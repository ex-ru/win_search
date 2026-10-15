# Release verification

Use these commands for WAI 1.2.1 from the repository root in PowerShell, after building the .NET 10 application and preparing its full staging package and release artifacts. The commands describe gates to run; review their generated reports before recording a gate as passed.

## Offline harness

This first run needs no model files. It checks the lexical file lifecycle and SQLite recovery/failure paths; packaged worker, native, and evaluation checks are skipped because no package or models were supplied.

```powershell
./tools/dotnet/dotnet.exe run --project checks/WAI.ReleaseSmoke -c Release -- --report results/release-smoke-1.2.1-lexical.json
```

## Full release gate

```powershell
./scripts/check-release.ps1 -PackageDir dist/1.2.1/staging -ModelsDir models -ReleasesDir dist/1.2.1/releases -ReportDir results/release-check-1.2.1 -Native
```

This gate requires the full package and local BGE-M3 model files. It checks the .NET 10 payload, native and managed notices, native provenance hashes and PE import closure, the matching FFmpeg source archive, release checksums, and the portable payload against staging. Its packaged smoke runs the CLI, CPU worker, file lifecycle and SQLite checks, PDFium extraction/rendering, FFmpeg media decoding, and CPU AV1 decoding. The default production evaluation uses `testbed-production.json` and a synthetic corpus; it is regression evidence, not a real-world retrieval accuracy claim. Models are not downloaded, and the run uses isolated home, index, and model folders.

The focused packaged-native gate can also be run on its own:

```powershell
./scripts/check-native-package.ps1 -PackageDir dist/1.2.1/staging -ReportDir results/native-package-1.2.1
```

It runs the native PE dependency audit and PDF/media smoke against the staged package, checks notice and provenance hashes, and includes the CPU AV1 decode. It does not test model workers.

## Packaged CPU workers

Only EmbeddingGemma 2 (`gemma2`) and BGE-M3 (`bge-m3`) are supported. Run both shipped workers against the local fixtures:

```powershell
py -3 scripts/check-packaged-workers.py --package dist/1.2.1/staging --models models --model all --report-dir results/packaged-workers-1.2.1
```

The check exercises text embeddings for both workers and image/audio embeddings for Gemma 2, verifies CPU mode and vector dimensions/normalization, and asks the packaged FFmpeg to decode the AV1 fixture. It writes `summary.json` and worker stderr logs under the report directory. These synthetic worker checks do not measure search quality or GPU compatibility.

When a native smoke runs, it also checks that the packaged worker rejects these removed aliases with a structured unsupported-model response (`ready: false`, an `error` containing “not supported”, and exit code 1): `pplx-v2`, `perplexity`, `pplx`, `nomic`, and `nomic-embed`.

## Historical portable update apply

`dist/releases` contains the previous 1.2.0 release. Use it as the actual historical baseline for the 1.2.1 portable apply gate:

```powershell
./scripts/check-update-apply.ps1 -PackageDir dist/1.2.1/staging -TargetReleaseDir dist/1.2.1/releases -ModelsDir models -BaselineVersion 1.2.0 -TargetVersion 1.2.1 -BaselineReleaseDir dist/releases -ReportDir results/update-apply-1.2.1
```

The script verifies the historical portable archive against its checksum, applies the 1.2.1 full package in a disposable portable workspace, and checks the installed payload hashes, existing index/settings, and a post-update document change. It requires the local Velopack CLI and does not exercise the installed GUI restart or interrupted network downloads. `WAI.UpdateSmoke` checks the application's update discovery/download path; an `applied: false` result does not prove an update was installed. For that final installed-update gate, use a disposable Windows profile or VM and verify the actual 1.2.0-to-1.2.1 apply/restart, launch, preserved data, and rejection recovery before treating the update as fully validated.

Each harness run keeps its uniquely named temporary workspace for diagnostics. Do not point it at a personal index. Reports record individual checks as `passed`, `failed`, or `skipped`; a required failure returns a nonzero exit code.
