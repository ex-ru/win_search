WAI on Windows. Native and fast. No web shell.

- Start here: `docs/User-Guide.md` (use it), `docs/Builder-Guide.md` (build it).
- Code: `src/WAI.Core` (engine), `src/WAI.Embed` (model helper,
  wai-embed.exe), `src/wai` (CLI), `src/WAI.App` (WPF app,
  WAIApp.exe — the name differs from wai.exe on purpose).
- Model runtime: `powershell -File scripts\build-llama-windows.ps1`
  puts DLLs in `windows/native/x64`.
- Dev run (builds all, checks all, starts the app):
  `powershell -File scripts\dev-run-windows.ps1`.
- Build: `dotnet build WAI.Windows.sln -c Release`.
- Ship: per-user Velopack, no admin. See `setup/velopack-notes.md`.
- Privacy: all on your PC. Model fetch once + daily update check only.
- Worker chatter goes to `<index>\worker.log`, never to your results.
