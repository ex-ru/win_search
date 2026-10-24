# WAI 1.2.7 for Windows

Local file search with a complete, verifiable distribution and a repeatable build process. This release resolves the hybrid search ranking imbalance where semantic image embeddings overshadowed exact filename matches, filters out noisy file extensions, boosts exact filename and stem matches, and dynamically normalizes relevance badges in the UI.

## Download and run

Choose **WinSearch.WAI-win-Setup.exe** for a per-user installation or **WinSearch.WAI-win-Portable.zip** for a portable copy. Windows x64; .NET and native runtimes are bundled. Models download separately on first run. Open search with **Ctrl+Space**.

The installer is unsigned: Windows may display an unknown-publisher warning. Check the release source and SHA256SUMS.txt before running it.

## Changes

- **Exact Filename & Stem Match Prioritization (SRC-01):**
  - Resolved an issue where exact filename searches (e.g. `обои.jpg`) failed to surface in the top results because loosely-related semantic image embeddings had high Z-scores that dominated candidate ranking.
  - Implemented multi-tiered filename boosting in `Searcher.cs`:
    - **Exact filename match:** `+12.0` score bonus.
    - **Exact stem match:** `+8.0` bonus (elevated to `+12.0` when matched with query extension).
    - **Prefix match:** `+5.0` bonus when filename starts with the query term.
    - **Substring / multi-term match:** `+3.5` / `+3.0` bonus when all query terms appear within the filename.
  - Rebalanced keyword coverage weight and added BM25 rank bonuses (`+0.5` to `+3.0`) so strong lexical matches consistently rank alongside or above semantic vectors.
- **Stopword Filtering for Common File Extensions:**
  - Common file extensions (`jpg`, `jpeg`, `png`, `pdf`, `docx`, `xlsx`, `mp4`, `zip`, etc.) and Russian media category words (`фото`, `картинка`, `рисунок`, `видео`, `документ`) are now classified as stopwords/kind-words.
  - Searching for `name.ext` no longer causes every `.ext` file in the index to be treated as a 50% keyword match, preventing candidate pool flooding.
  - A fallback retains extensions if the query consists solely of an extension.
- **Dynamic Relevance Score Normalization in UI:**
  - Fixed badge saturation where results previously all displayed `99%` match precision.
  - Normalized hit scores dynamically relative to the top result score (`maxScore`), ensuring clear differentiation between top matches and weaker secondary hits while clamping between 1% and 99%.
- **Candidate Fallback Protection:**
  - Ensured top candidates without extracted meaning or text snippets retain a valid segment reference rather than being dropped during result formatting.

WAI project code uses MIT. Individual bundled components retain their own licenses; FFmpeg is LGPL-2.1-or-later in this build, dav1d is BSD-2-Clause and zlib uses the zlib license. Matching media source: [ffmpeg-corresponding-source.zip](https://github.com/ex-ru/win_search/releases/download/v1.2.1/ffmpeg-corresponding-source.zip).

## Updates and validation

The package identity remains **WinSearch.WAI**. Existing WAI settings and indexes under `%LocalAppData%\WAI` are retained.
