# Contributing to WAI for Windows

Start with [README.md](README.md) and the [builder guide](port/WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/docs/Builder-Guide.md). Active Windows code is under `port/WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows`; `upstream/` is a historical macOS snapshot.

Use the SDK pinned in `global.json`. Before opening a pull request, run:

```powershell
dotnet test ./port/WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/WAI.Windows.sln -c Release -warnaserror:NU1903,NU1904
dotnet run --project ./checks/WAI.ReleaseSmoke -c Release -- --report ./state/lifecycle.json
```

Keep changes focused and explain the behavior, validation and remaining limitations. Search, indexing and cancellation changes need regression coverage. Keep the default user flow simple; new functionality should address a demonstrated need.

Do not commit models, downloaded toolchains, release packages, local indexes, credentials, or reports containing private file names. Keep local validation output under `state/`; add sanitized, reproducible evidence to `results/` only deliberately.

Report bugs through [GitHub Issues](https://github.com/ex-ru/win_search/issues). Include the WAI version, Windows version, CPU/GPU backend, steps to reproduce and expected behavior. Attach a small synthetic document when possible. Remove file contents, personal paths and credentials from logs. Follow [SECURITY.md](SECURITY.md) for vulnerabilities.
