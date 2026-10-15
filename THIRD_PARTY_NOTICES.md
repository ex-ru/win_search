# Third-party distribution status

The [MIT license](LICENSE) covers WAI project code. Bundled native programs, runtimes, packages and downloaded models have their own licenses. The root MIT license does not replace them.

| Component | Current evidence | Required before the next general release |
|---|---|---|
| llama.cpp / GGML / MTMD | Build scripts and local runtime archives identify upstream tag `b11461`; [upstream](https://github.com/ggml-org/llama.cpp/tree/b11461). | Retain the matching MIT notice and record hashes/provenance for the exact CPU/Vulkan binaries. |
| LLVM OpenMP | The package copies `LICENSE-LLVM-OpenMP` from the native runtime distribution. | Keep this notice with `libomp.dll`. |
| FFmpeg / FFprobe | The inspected binaries report `9.0.2-full_build-www.gyan.dev`, `--enable-gpl` and `--enable-version3`. Only the executables were retained locally. | Recover the matching build/source/dependency materials and license texts, preserve the build configuration, and provide the required corresponding-source distribution. A generic link to current upstream source does not identify these binaries. |
| PDFium | `windows/native/x64/pdfium.dll` is tracked; the current packaging script copies no PDFium notice or source/build manifest. | Identify the exact binary build and include its matching PDFium and third-party notices. |
| .NET, SQLite and managed dependencies | Versions are recorded in the project files and packaged dependency manifests. | Collect their applicable notices in the release license bundle. |
| Downloaded embedding models | Model revisions and checksums are pinned in `ModelCatalog.cs`; weights are not committed or bundled. | Keep each upstream model's license and use terms with its download provenance when changing models. |

FFmpeg documents the effect of GPL-enabled configurations and the need for matching source at [FFmpeg legal information](https://ffmpeg.org/legal.html). This inventory records a concrete packaging gap; it is not a statement that the current license bundle is complete.

The already published 1.2.0 assets are unchanged. **Do not describe their two-file license directory as a complete third-party notice bundle.** Track the remaining release work as `PUB-02` in [BACKLOG.md](BACKLOG.md).
