# WAI 1.2.1 release verification

All required automated release gates passed on the downloaded distribution built from **`fcd419c81e21dc67fddd8bfb8ac7af467efada90`**. This is the binary source commit and the target of tag `v1.2.1`; the final report/documentation commit follows it without changing application binaries.

Clean Windows 11 installation, first model download and installed GUI upgrade are assigned to the owner's volunteer testers. Their result remains **pending**. The installer is **unsigned**.

## Build and package evidence

| Check | Result |
|---|---|
| [Fresh Windows package build](https://github.com/ex-ru/win_search/actions/runs/38811112578), attempt 2 | Passed: pinned restore, build, packaged native gate, draft assets |
| [Windows checks](https://github.com/ex-ru/win_search/actions/runs/38811112421) | Passed: regressions, lifecycle and self-contained publish |
| Unit/WPF suite | 191 passed, 0 failed, 1 explicit all-model benchmark skip |
| Downloaded assets | All 10 size/SHA-256 values matched GitHub; nine payload/feed checksums matched SHA256SUMS.txt |
| Actual downloaded Core vs CI-loaded Core | SHA-256 `6397aa264c22780669f53e465f66bd98a7d275f44629e377fc5d9a783ea9be5e` |
| Gemma CPU package | CLI/index/search/status, semantic file lifecycle, text/image/audio vectors passed |
| BGE-M3 CPU package | CLI/index/search/status, semantic file lifecycle and text vectors passed |
| Frozen BGE corpus | 41 synthetic documents, 240 queries, 0 failed assertions |
| SQLite recovery | Interrupted transaction rolled back; constrained SQLITE_FULL recovered with a subsequent write |
| Native payload | 26 PE files / 285 imports; llama/MTMD loaded with the packaged VC runtime; PDF text/render, PCM, MPEG-4 and CPU AV1 passed |
| Removed model aliases | All five rejected with structured ready:false and exit 1 before native inference |
| Actual historical update | Original 1.2.0 portable → 1.2.1 applied by Update.exe; corrupt package rejected; index/settings retained; changed document found after relaunch |
| Managed notices | 9 packages, 14 legal files, 8 exact-version overrides; no unresolved notices; .NET runtime notices included |
| Velopack notices | 612 Cargo.lock registry-dependency superset, 1,149 legal/source-root hash references verified in the shipped payload; no unresolved entries |

Machine-readable sanitized evidence: [results/release-1.2.1.json](../results/release-1.2.1.json). CI verification artifact SHA-256: `52f651270bff7f2f074413001823f13fba54a9f7ce7a48e16d73553b276a880e`.

The historical 1.2.0 portable SHA-256 is `a432fb246585506f0025d7e0d56bec01dead70899fdc64efd4fc293443e21a45`. No historical release binaries were rebuilt or replaced for that update gate. The final 1.2.1 package hash was checked again after actual apply.

## Pinned inputs and scope

.NET SDK 10.0.402, self-contained runtime 10.0.13 and Velopack 1.2.161; Microsoft VC CRT 14.51.36247.0; official llama.cpp b11461 and PDFium 157.0.8100.0; FFmpeg 9.0.2, BSD dav1d 1.5.4 and zlib 1.3.2. Native lock bytes from the tested commit matched the shipped provenance. Exact media sources and the cross-build recipe are supplied beside the binary assets. NuGet lock files and managed/runtime/updater notices are included. See [distribution instructions](../DISTRIBUTION.md) and [third-party materials](../THIRD_PARTY_NOTICES.md).

This is a repeatable process from pinned inputs; arbitrary toolchains, installer timestamps and compression are not claimed to produce byte-identical output. The Velopack notice inventory deliberately covers a Cargo.lock registry superset, including target/development dependencies, and does not assert that every listed crate runs on Windows.

Only **Gemma** and **BGE-M3** remain in the supported selector. Historical Perplexity crashed before readiness and its late-interaction dimensions did not match the integration. Nomic produced text vectors but failed the Russian retrieval scenario and needs task-specific prompt integration. Both are excluded; old settings migrate to Gemma, which may require a model download and semantic reindex. These failures are not counted as successful retrieval tests.

The accepted model checks used existing local files matching catalog SHA-256 values, copied into isolated model/home/index folders. They did not exercise a fresh public model download. Gemma tested text, image and audio inference; the frozen 240-query corpus was run with BGE-M3. Exact/morphology/absent assertions use lexical output; semantic assertions use hybrid output. These synthetic checks are not real-world accuracy guarantees.

## Repeat the checks

After [building](../DISTRIBUTION.md), run from the repository root in PowerShell 7; keep the original 1.2.0 release assets under `dist/releases` for the historical update test:

```powershell
./scripts/check-release.ps1 -PackageDir dist/1.2.1/staging -ModelsDir models -ReleasesDir dist/1.2.1/releases -Model bge-m3 -Native -ReportDir state/acceptance/bge
./scripts/check-release.ps1 -PackageDir dist/1.2.1/staging -ModelsDir models -ReleasesDir dist/1.2.1/releases -Model gemma2 -SkipEvaluation -Native -ReportDir state/acceptance/gemma
py -3 scripts/check-packaged-workers.py --package dist/1.2.1/staging --models models --report-dir state/acceptance/workers
./scripts/check-update-apply.ps1 -PackageDir dist/1.2.1/staging -TargetReleaseDir dist/1.2.1/releases -ModelsDir models -BaselineVersion 1.2.0 -BaselineReleaseDir dist/releases -TargetVersion 1.2.1 -ReportDir state/acceptance/update
```

Publication acceptance used the downloaded full.nupkg payload rather than a local developer publish directory; the portable executables/native/runtime files were compared with it. All mutable test state was disposable and isolated. Existing user installations were not modified.

## Asset identity

| Asset | Bytes | SHA-256 |
|---|---:|---|
| assets.win.json | 211 | `2676db7d1b41c6488651fc3695c032cace0c82edee774ad34f3e5836e647d8b2` |
| ffmpeg-corresponding-source.zip | 15359793 | `332d1652170ea3b9d4540064f5b471966d0e7c54b6ffeb8ef84a6687e141b2dd` |
| ffmpeg-tools-win-x64.zip | 16720455 | `64b9bf8d66b59b97e03f4c8588152ce7e92db4b9e744b229c5a7a868178c5f9c` |
| native-provenance.json | 7858 | `29b001716d1fe73d02673888ade922c02af72291111f85780cd56617e92e6c59` |
| RELEASES | 84 | `b8bfc2101580ac83b755a354f123ca56f7c11c9a2ab54d521905b5e2f388ec5e` |
| releases.win.json | 261 | `16d67433b7901cf3b7b243720c65873c677894885af72af7b78975ac1b23c342` |
| SHA256SUMS.txt | 816 | `554ec8829ca0f1fc48e054eaba949092040d76e398facf5121843da11114a8c1` |
| WinSearch.WAI-1.2.1-full.nupkg | 135519169 | `4ca91f02ed652d697d1162d6807d69ba25e3268ddb7e49909b5a3c4b73e5d3bc` |
| WinSearch.WAI-win-Portable.zip | 135518071 | `01f126ff19cbff85e71a59f3be22f3aa58a81443fc7527d80b986006fb63125b` |
| WinSearch.WAI-win-Setup.exe | 143033281 | `5a5353c0d53ac715904a78fce94b9cef20abfedd7bc57de15f4dbd5f7213380b` |

## Remaining manual checks

Use the [Windows 11 tester guide](TESTING_WINDOWS_11_RU.md) for per-user install, first-run downloads, search/preview, watcher changes, tray exit/restart and GUI upgrade. GPU availability and performance depend on drivers and have no vendor-wide pass claim. The automated apply check exercises a portable installation, updater hooks and CLI relaunch, not interactive installed GUI restart. Installations under a different package identity require manual migration to WinSearch.WAI.
