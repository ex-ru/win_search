# WAI Windows (win_search) 🔍

> **100% локальный семантический и полнотекстовый поиск по файлам для Windows 10/11.**  
> Поиск документов, кода, скриншотов, фото, аудио и видео по смыслу, описанию и точным реквизитам без облаков, телеметрии и сторонних серверов.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue.svg)](https://microsoft.com)
[![Runtime: .NET 9](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com)
[![Hardware: Vulkan / CUDA / CPU](https://img.shields.io/badge/Hardware-Vulkan%20%7C%20CUDA%20%7C%20CPU-success.svg)](https://github.com/ggerganov/llama.cpp)

---

## 📌 Первоисточники и история проекта

Проект является глубоко доработанным и оптимизированным Windows-портом оригинального приложения **WAI**:

- **Оригинальный репозиторий (macOS):** [ARahim3/WAI](https://github.com/ARahim3/WAI) (автор: Abdur Rahim, лицензия MIT).
- **Исходный открытый PR портирования на Windows:** [PR #18: Windows port](https://github.com/ARahim3/WAI/pull/18).
- **Текущий репозиторий:** [ex-ru/win_search](https://github.com/ex-ru/win_search) — полная реализация, SIMD- и многопоточная оптимизация, нативный WinRT OCR, полная адаптация под русский язык и упаковка в автономный инсталлятор Velopack.

---

## ⚡ Ключевые возможности

1. **Быстрый гибридный поиск:**
   - Семантический поиск по смыслу через модель **EmbeddingGemma 2** (768-мерные векторы, Q8_0).
   - Лексический поиск с морфологией (SQLite FTS5, стемминг Портера, ранжирование BM25).
   - Аппаратное ускорение векторных вычислений через **SIMD** (`System.Numerics.Vector<float>` с 4-way unrolling) и многопоточный скоринг `Parallel.For`.
   - Прототип графа **HNSW** покрыт тестами, но пока не подключён к поисковому конвейеру: текущий поиск перебирает векторы.
2. **Аппаратное GPU-ускорение без привязки к вендору:**
   - Полноценная поддержка **AMD Radeon** (через бэкенд **Vulkan** / `ggml-vulkan.dll`).
   - В готовый пакет включены Vulkan и CPU; CUDA требует отдельной сборки соответствующего бэкенда.
3. **Распознавание текста на скриншотах (OCR):**
   - Нативный OCR на базе Windows Runtime API (`Windows.Media.Ocr.OcrEngine`) без тяжелых внешних бинарников.
   - Поддержка русских и английских скриншотов (поиск по номерам счетов, суммам, реквизитам).
   - Каскадный fallback на Tesseract OCR при его установке; Tesseract и языковые данные не входят в текущий пакет.
4. **Современные медиаформаты (WIC):**
   - Декодирование изображений через Windows Imaging Component (WebP, HEIC/HEIF, TIFF, PNG, JPEG).
   - Аппаратный Fant-даунскейлинг без предварительной загрузки гигантских 48MP фото в память.
   - Автоматический учет EXIF-ориентации камеры и извлечение даты съемки.
5. **Аудио и видео:**
   - Нарезка ключевых кадров и извлечение аудио через встроенный неблокирующий конвейер FFmpeg.
6. **Полная приватность и локальность (No-Admin, Per-User):**
   - Никаких сетевых запросов при поиске. Все данные хранятся локально в `%LocalAppData%\WAI`.
   - Установщик **Velopack** (`WinSearch.DigUp-win-Setup.exe`) и переносимый архив (`WinSearch.DigUp-win-Portable.zip`) со встроенным PDFium.
   - Фоновая проверка обновлений из локальной папки: путь задаётся в Settings; скачанный пакет применяется при следующем запуске.
   - Глобальный хоткей `Ctrl + Alt + Space` для мгновенного вызова поиска из любого окна.

---

## 🚀 Быстрый старт

### Запуск приложения

Для запуска приложения с бэкендом Vulkan (AMD/NVIDIA/Intel):

```powershell
.\Start-WAI.ps1 -Backend vulkan
```

- Поиск вызывается по нажатию **`Ctrl + Alt + Space`** (или через иконку в системном трее).
- Настройки директорий: правый клик по иконке в трее $\to$ **`Settings…`** $\to$ **`Folders to search`**.

### Консольный интерфейс (CLI)

```powershell
# Проверить статус текущего индекса
cmd /c "set WAI_MODELS=D:\_projects\win_search\models&& .\run-vulkan\wai.exe status --index state\index-vulkan"

# Проиндексировать новую папку
cmd /c "set WAI_MODELS=D:\_projects\win_search\models&& .\run-vulkan\wai.exe index ""C:\MyFolder"" --index state\index-vulkan"

# Поиск из командной строки
cmd /c "set WAI_MODELS=D:\_projects\win_search\models&& .\run-vulkan\wai.exe search ""договор оказания услуг"" --index state\index-vulkan"

# Запуск MCP сервера для ИИ-агентов (Claude Desktop, Cursor, Antigravity)
wai mcp
```

### 🤖 Подключение ИИ-агентов (Model Context Protocol / MCP)

WAI включает встроенный сервер **Model Context Protocol (MCP)** для безопасного подключения больших языковых моделей и автономных агентов строго в режиме **Read-Only**:
- **Zero-setup:** встроен прямо в `wai.exe mcp` (не требует Python/Node.js).
- **Готовые инструменты:** `search_documents`, `read_document_content`, `get_document_info`, `get_index_status`.
- **Поддержка сред:** Claude Desktop, Cursor, Antigravity, Docker Agent, VS Code, Cline.
- Подробные инструкции по настройке и примеры конфигураций см. в [Руководстве по MCP](docs/MCP_GUIDE.md).

---

## 🧪 Тестирование и бенчмарки

Решение покрыто модульными и сквозными тестами:

```powershell
# Запуск модульных и WPF-регрессионных тестов xUnit
& ".\tools\dotnet\dotnet.exe" test port\WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f\windows\WAI.Windows.sln
```

- **Результаты русскоязычного бенчмарка (`results/eval-russian.json`):**
  - Точность **Top-1: 100%** (3/3 exact OCR, 3/3 semantic cross-lingual);
  - Точность **Top-5: 100%**;
  - Время теплого отклика (Vulkan) в этом первоначальном прогоне: **23.2 мс**. Это результат небольшой тестовой коллекции, а не верхняя граница времени поиска.

Повторная проверка после исправлений: `results/fixes-search.json`, 13 векторов, 6/6 положительных запросов Top-1 и Top-5; медиана encode+search **62.2 мс**, p95 **77.3 мс** на этом компьютере при параллельно работающем приложении. Проверка более крупного индекса (8137 векторов) в `results/review-current.json` дала медиану около **150 мс**. Скорость зависит от коллекции и нагрузки.

---

## 📦 Сборка дистрибутива (Velopack Setup & Portable)

Скрипт автономной упаковки собирает `Setup.exe` и портативный `.zip`:

```powershell
powershell -ExecutionPolicy Bypass -File port\WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f\windows\scripts\build-package.ps1
```

Готовые пакеты размещаются в каталоге `dist/releases/`:
- `WinSearch.DigUp-win-Setup.exe` — установщик для пользователя (no-admin), программа хранится отдельно от данных в `%LocalAppData%\WinSearch.DigUp`;
- `WinSearch.DigUp-win-Portable.zip` — переносимая версия;
- `SHA256SUMS.txt` — контрольные суммы релизных файлов.

Источник обновлений для этого рабочего каталога — абсолютный путь к `dist/releases`; `Start-WAI.ps1` передаёт его приложению. В Settings можно изменить путь и включить/выключить ежедневную проверку. Обновления доступны в пакетах Velopack; распакованная сборка `run-vulkan` предназначена для разработки. Модели загружаются отдельно при первом запуске и не включены в архив.

После восстановления PDFium приложение и CLI автоматически повторяют обработку только PDF с прежней ошибкой отсутствующей DLL. Полный сброс индекса не требуется.

Релиз `1.0.2` проверен: **48/48** тестов (`results/fixes-tests/fixes.trx`), восстановление PDF и поиск по его содержимому (`results/pdf-search-after.json`), скачивание и применение локального обновления `1.0.1 → 1.0.2` в отдельном переносимом каталоге (`results/fixes-update.json`). Старые пакеты `WAI` сохранены в `dist/releases/legacy` и исключены из активного feed.

---

## 📄 Лицензия

Проект лицензирован под [MIT License](LICENSE).  

- Исходный код WAI (macOS): Copyright (c) 2026 Abdur Rahim.
- Реализация Windows-порта: Copyright (c) 2026 win_search contributors.
