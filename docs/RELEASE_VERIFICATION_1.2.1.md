# WAI 1.2.1 release verification

Clean Windows 11 installation and GUI feedback are assigned to the owner's volunteer testers; that manual test is not represented as an automated pass.

## Pinned build inputs

- .NET SDK 10.0.402; self-contained runtime 10.0.13; Velopack 1.2.161.
- WinSearch.WAI package identity; 1.2.1 in UI, assemblies and package metadata.
- Microsoft Visual C++ CRT 14.51.36247.0, extracted from a hash-pinned Microsoft-signed installer without machine-wide installation; three required DLLs are individually pinned.
- Official llama.cpp b11461 CPU/Vulkan and PDFium 157.0.8100.0 archives, checked by SHA-256.
- FFmpeg 9.0.2, BSD dav1d 1.5.4 and zlib 1.3.2; exact sources and cross-build recipe supplied beside binaries.
- NuGet lock files and managed/runtime/updater notice inventories.

This is a repeatable process using pinned inputs. Installer timestamps and compression are not claimed to be bit-for-bit reproducible across arbitrary toolchains.

## Completed preparation checks

- Release unit/WPF tests: 185 passed, 1 explicitly skipped all-model benchmark, 0 failed.
- New native payload: PDF text/rendering, PCM, MPEG-4 and CPU AV1 passed on Windows.
- Managed notices: 9 packages, 8 exact-version hash-verified license overrides, runtime notices present, no unresolved package notices.
- Velopack registry superset: 612 crate archives verified against Cargo.lock, with original authors, license evidence and recovered/retained source recorded.

## Final-package acceptance

The final workflow builds from a fresh Windows checkout and checks source regressions, self-contained publishing and actual packaged native decoding before creating a draft. Publication follows checks of the downloaded payload with pinned local models, the frozen corpus and real portable upgrade application. A draft or a successful upload is not itself an acceptance pass.

Final reports and source/asset hashes are recorded after those gates complete.

## Limits

The corpus is synthetic. CPU inference is tested locally; a full GPU/driver matrix is not. Portable apply preserves fixture settings/index; interactive installed GUI behavior remains assigned to volunteer Windows 11 testing. The installer is unsigned. Older installations with a different package identity require manual migration to WinSearch.WAI.
