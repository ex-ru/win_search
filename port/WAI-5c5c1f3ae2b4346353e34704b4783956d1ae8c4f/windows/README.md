# WAI for Windows 1.2.0

- Start with docs/User-Guide.md for the simple three-step setup, or docs/Builder-Guide.md to build and validate the package.
- The repository uses .NET SDK 10.0.402 from global.json.
- The standard full package has identity WinSearch.WAI, includes CPU/Vulkan runtimes and bundles FFmpeg. Models are fetched on first use and verified against pinned size and SHA-256 metadata.
- Build the app with dotnet build WAI.Windows.sln -c Release. Build the standard release package with scripts/build-package.ps1; the default is the full package.
- User settings and index data are under %LOCALAPPDATA%\WAI. Existing data stays in place; automatic upgrade from a different package identity is not guaranteed.
- Indexing and search run locally. Network use is for model downloads and optional update checks.