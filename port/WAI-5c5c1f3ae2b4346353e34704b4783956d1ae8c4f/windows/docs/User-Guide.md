# WAI for Windows 1.2.6 — User Guide

WAI searches files in folders you choose. Describe what you remember, or search for exact words. Search and indexing run on your PC. The app downloads a model on first use and can check for updates if that setting is enabled; it does not send indexed file contents to a cloud search service.

## Get started in three steps

1. Install WAI for your Windows account. Administrator access is not required.
2. Open WAI, choose the folders you want to search, then wait while the selected model downloads and the first index is created. The standard full installer includes FFmpeg for supported audio and video files.
3. Press Ctrl+Space, enter a description or words from a file, then open a result. The tray icon also provides access to search and settings with instantaneous context menu response.

WAI uses the selected model to create vectors. Model downloads are pinned to known sources and validated against expected file size and SHA-256 values.

## Everyday search

- Use ordinary phrases for semantic matches or distinctive words and numbers for text matches.
- Use Up and Down to move through results (or Left, Right, Up, and Down in gallery mode), Enter to open a result, Space to preview supported items with an interactive scrub timeline, and Esc to close the search panel.
- Switch between the classic list view (☰) and the thumbnail gallery view (⊞) using the view toggle button in the filter bar.
- Code search results open directly at the matched line and column in supported editors (VS Code, Cursor, JetBrains IDEs, Visual Studio, Notepad++, Sublime Text, Kate).
- PDF search results open directly to the target page in Adobe Acrobat, Foxit Reader, SumatraPDF, Edge, and Chrome.
- Audio and video results seek directly to the matched timestamp in VLC, MPC-HC/BE, MPV, and PotPlayer.
- New or changed files become searchable after indexing completes. The first index may take time; WAI can continue indexing after you close and reopen the app.

## Advanced settings (optional)

Open Settings from the tray to change the folders WAI watches, choose a model or CPU/Vulkan preference, enable code indexing for selected folders, toggle autostart with Windows (synchronized with Windows Task Manager `StartupApproved`), or change the hotkey. These options are not required for the default search workflow.

WAI indexes supported file types from selected folders. Image decoding and OCR depend on available Windows components and installed OCR languages. Audio and video support depends on the format and codec; bundled FFmpeg removes the need to install it separately. No claim is made that every format, language, or file is supported.

FTS5 full-text search complements semantic retrieval. Its Porter tokenizer is not a general Russian morphology engine, so inflected words are not guaranteed to match as if they shared a stem.

## Privacy and data

Settings, models, and index data are kept under %LOCALAPPDATA%\WAI. Search queries and indexed content stay local. Network access is used to download model files and, when enabled, check for updates.

The package identity is WinSearch.WAI. Existing WAI data under %LOCALAPPDATA%\WAI remains in place. Automatic update from an installation with another package identity is not guaranteed. If installing over an older identity, verify the new installation and check the data you need before removing the old app.

## Troubleshooting

- If a folder is missing from results, confirm it is selected in Settings and wait for indexing to finish.
- If a file fails to index, WAI can continue with other files. Check the local error log under %LOCALAPPDATA%\WAI and include relevant details in a bug report.
- If Vulkan does not work on a machine, select CPU in Settings. Vulkan support depends on the GPU and driver.

Historic measurements in the repository used small test fixtures and specific hardware. They are not promises about search accuracy, indexing speed, memory use, or response time on other collections.