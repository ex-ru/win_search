# Release verification

Run the offline harness after building with .NET 10:

```powershell
./tools/dotnet/dotnet.exe run --project checks/WAI.ReleaseSmoke -c Release -- --report results/release-smoke.json
./tools/dotnet/dotnet.exe run --project checks/WAI.ReleaseSmoke -c Release -- --package dist/staging --models models --model bge-m3 --native --eval testbed-production.json --report results/release-smoke-full.json
./scripts/check-release.ps1 -PackageDir dist/staging -ModelsDir models -ReleasesDir dist/releases -Native
```

The first command requires no models. Its real file pipeline runs in words-only mode, explicitly separated from the packaged CPU-worker check. Missing explicitly requested models or native dependencies fail the run. Reports record `passed`, `failed`, and `skipped` per check; a failed required check returns a nonzero exit code. The script requires the FULL package, checks release SHA-256 sums, compares key portable payload files with the tested staging folder, checks the actual .NET 10 runtime binary, and launches the packaged CLI against isolated home/index/model folders. It never downloads models. The existing local model files are read-only fixture inputs; the script links or copies them into its isolated model directory.

The harness exercises:

- Real crawler, DefaultExtractor, Indexer, IndexStore and Searcher against generated text files; modifying content, renaming, deletion, store close/reopen, old-result removal, integrity and foreign-key checks.
- Actual packaged `wai-embed.exe --cpu`, real local model embeddings and hybrid queries. This confirms forced CPU operation, not GPU-vendor compatibility or automatic fallback on every driver version.
- A child process killed after an actual 2 MB uncommitted SQLite write, followed by recovery with committed data retained and uncommitted data absent.
- SQLite `SQLITE_FULL` from a real write constrained by `PRAGMA max_page_count`, atomic rollback, integrity and subsequent successful writing. This reproducible fault injection does not fill the host disk.
- With `--native`: PDFium text extraction and page rendering, FFprobe inspection, FFmpeg PCM decoding, generated MP4 encoding and actual frame decoding. A PDF also traverses the real indexer when real model and native modes are selected together.
- With `--eval`: a frozen synthetic corpus of 40 independent topics, an equal-content duplicate, and 240 independent query strings: English/Russian semantic questions, Russian forms, exact numbers, and absent terms/identifiers. Equivalent duplicate filenames are accepted. All positive queries must match a known answer in top 5. Exact/morphology and absent checks use lexical results; semantic positives use hybrid results. Hybrid candidates for absent queries are recorded, because nearest-neighbour retrieval is not an absence classifier. This corpus is regression evidence, not a claim of production retrieval accuracy. Detailed results stay in the generated isolated workspace.

Each run preserves its uniquely named temporary workspace for diagnostics. Do not point the harness at a personal index: there is no parameter for doing so.

## Separate installed-update gate

The repository provides an executable local portable apply gate:

```powershell
./scripts/check-update-apply.ps1 -PackageDir dist/staging -TargetReleaseDir dist/releases -ModelsDir models
```

It creates a disposable `WinSearch.WAI` 1.1.99 package from current staging with a baseline-only payload marker, extracts its actual portable archive, runs the packaged CLI, checks rejection of a corrupt package, and invokes the real updater with explicit portable root/cache and `--norestart`. It requires installed metadata to advance to 1.2.0, the obsolete baseline marker to disappear, target executable/runtime hashes to match, and existing index/settings to survive separate target CLI launches and document mutation. It preserves updater logs and `update-apply.json`. The baseline is a package-mechanism fixture using current binaries; it does not verify historic-binary migrations or interactive GUI restart. No user installation is opened.

`WAI.UpdateSmoke` verifies the application's actual check/download path. Its `applied: false` report does not prove an update was installed. Before calling an update release production-ready, perform this additional gate on a disposable Windows installation or VM:

1. Build and retain a signed baseline and a higher-version target under the same `WinSearch.WAI` package identity. Different legacy package identities are a migration scenario, not a valid upgrade baseline.
2. Install the baseline into a disposable user profile. Create the fixture index and persistent settings; record the installed executable hashes and version.
3. Point the baseline's update source at the local target feed. Run `WAI.UpdateSmoke` and verify target nupkg SHA-256 against the feed.
4. Trigger the application's actual apply/restart action, wait for the installer process to exit, and launch the target through its installed shortcut/locator. Record the updater exit code, target installed version, and executable hashes. A successful download alone must fail this gate.
5. Search the previously indexed fixture, verify settings survived, mutate a fixture, restart again, and confirm new results. Retain updater logs and a report containing `applied: true` only after all installed-target checks pass.
6. Repeat with interrupted download and corrupted payload, confirming the baseline remains launchable. Test package-identity migration separately if supported.

The release script reports installed UI behavior and actual update application as limitations rather than silently claiming those checks passed.
