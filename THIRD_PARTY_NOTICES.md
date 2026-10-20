# WAI 1.2.4 - third-party distribution

WAI project code uses the [MIT license](LICENSE). Bundled components keep their own licenses. Actual notices are included under licenses/ in the package.

| Component | Pinned version and materials |
|---|---|
| llama.cpp / GGML / MTMD | Official b11461 CPU/Vulkan archive, checked by size and SHA-256. MIT and LLVM OpenMP notices in licenses/llama/. |
| Microsoft Visual C++ CRT | 14.51.36247.0, extracted from the pinned Microsoft-signed redistributable. Required app-local DLLs have individual hashes; proprietary runtime terms and developer distributable-code references are in licenses/vcruntime/. |
| PDFium | 157.0.8100.0 from bblanchon chromium/8100. Binary distribution license and every supplied component notice in licenses/pdfium/. |
| FFmpeg / FFprobe | FFmpeg 9.0.2, without GPL, version-3 or nonfree components. LGPL-2.1-or-later, used as separate programs. Build configuration, toolchain versions, imported DLLs and binary hashes in licenses/ffmpeg/. |
| dav1d | 1.5.4, BSD-2-Clause. Retains CPU AV1 decoding; exact source and notice included. |
| zlib | 1.3.2, zlib license. PNG/compression support; exact source and notice included. |
| .NET / managed packages | Exact versions/content hashes locked. licenses/managed/index.json records authors/copyright and copied texts. Runtime notices come from the exact .NET 10.0.13 runtime packs. |
| Velopack installer/updater | 1.2.161, root MIT notice plus Cargo.lock notice inventory under licenses/velopack-runtime/. The registry-dependency superset does not claim every development/platform crate is shipped. Archives are checksum-verified; omitted standalone notices are recovered at crate VCS commits where available. Otherwise declared license terms, authors and complete original crate source are retained. |
| Models | Downloaded separately, never bundled. Each model retains upstream license/use terms; source revisions and expected size/hash are pinned in ModelCatalog.cs. |

## Corresponding media source

The same release hosts [ffmpeg-corresponding-source.zip](https://github.com/ex-ru/win_search/releases/download/v1.2.1/ffmpeg-corresponding-source.zip): exact unmodified FFmpeg, dav1d and zlib archives, pinned hashes, the MinGW recipe and rebuild instructions. This software uses [FFmpeg](https://ffmpeg.org/) under LGPL-2.1-or-later. See [FFmpeg distribution guidance](https://ffmpeg.org/legal.html).

native-dependencies.json pins archive URLs, sizes and SHA-256. native-provenance.json in each package records selected file hashes. Preparation, packaging and release gates reject missing required notices or mismatched inputs/source archives.

Historical 1.2.0 assets remain unchanged; their incomplete notice/source bundle is superseded by this new distribution, without replacing binaries under the old tag.
