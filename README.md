# DigUp Windows (win_search) 🔍

> **100% локальный семантический и полнотекстовый поиск по файлам для Windows 10/11.**  
> Поиск документов, кода, скриншотов, фото, аудио и видео по смыслу, описанию и точным реквизитам без облаков, телеметрии и сторонних серверов.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue.svg)](https://microsoft.com)
[![Runtime: .NET 9](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com)
[![Hardware: Vulkan / CUDA / CPU](https://img.shields.io/badge/Hardware-Vulkan%20%7C%20CUDA%20%7C%20CPU-success.svg)](https://github.com/ggerganov/llama.cpp)

---

## 📌 Первоисточники и история проекта

Проект является глубоко доработанным и оптимизированным Windows-портом оригинального приложения **DigUp**:

- **Оригинальный репозиторий (macOS):** [ARahim3/DigUp](https://github.com/ARahim3/DigUp) (автор: Abdur Rahim, лицензия MIT).
- **Исходный открытый PR портирования на Windows:** [PR #18: Windows port](https://github.com/ARahim3/DigUp/pull/18).
- **Текущий репозиторий:** [ex-ru/win_search](https://github.com/ex-ru/win_search) — полная реализация, SIMD- и многопоточная оптимизация, нативный WinRT OCR, полная адаптация под русский язык и упаковка в автономный инсталлятор Velopack.

---

## ⚡ Ключевые возможности

1. **Мгновенный гибридный поиск (< 25 мс):**
   - Семантический поиск по смыслу через модель **EmbeddingGemma 2** (768-мерные векторы, Q8_0).
   - Лексический поиск с морфологией (SQLite FTS5, стемминг Портера, ранжирование BM25).
   - Аппаратное ускорение векторных вычислений через **SIMD** (`System.Numerics.Vector<float>` с 4-way unrolling) и многопоточный скоринг `Parallel.For`.
   - Прототип графа **HNSW** для логарифмического масштабирования на коллекциях свыше 100 000+ файлов.
2. **Аппаратное GPU-ускорение без привязки к вендору:**
   - Полноценная поддержка **AMD Radeon** (через бэкенд **Vulkan** / `ggml-vulkan.dll`).
   - Поддержка **NVIDIA** (CUDA) и автоматический быстрый **CPU-fallback**.
3. **Распознавание текста на скриншотах (OCR):**
   - Нативный OCR на базе Windows Runtime API (`Windows.Media.Ocr.OcrEngine`) без тяжелых внешних бинарников.
   - Поддержка русских и английских скриншотов (поиск по номерам счетов, суммам, реквизитам).
   - Каскадный fallback на автономный Tesseract OCR при отсутствии языковых пакетов ОС.
4. **Современные медиаформаты (WIC):**
   - Декодирование изображений через Windows Imaging Component (WebP, HEIC/HEIF, TIFF, PNG, JPEG).
   - Аппаратный Fant-даунскейлинг без предварительной загрузки гигантских 48MP фото в память.
   - Автоматический учет EXIF-ориентации камеры и извлечение даты съемки.
5. **Аудио и видео:**
   - Нарезка ключевых кадров и извлечение аудио через встроенный неблокирующий конвейер FFmpeg.
6. **Полная приватность и локальность (No-Admin, Per-User):**
   - Никаких сетевых запросов при поиске. Все данные хранятся локально в `%LocalAppData%\DigUp`.
   - Автономный установщик **Velopack** (`DigUp-win-Setup.exe`) и переносимый архив (`DigUp-win-Portable.zip`).
   - Глобальный хоткей `Ctrl + Alt + Space` для мгновенного вызова поиска из любого окна.

---

## 🚀 Быстрый старт

### Запуск приложения

Для запуска приложения с бэкендом Vulkan (AMD/NVIDIA/Intel):

```powershell
.\Start-DigUp.ps1 -Backend vulkan
```

- Поиск вызывается по нажатию **`Ctrl + Alt + Space`** (или через иконку в системном трее).
- Настройки директорий: правый клик по иконке в трее $\to$ **`Settings…`** $\to$ **`Folders to search`**.

### Консольный интерфейс (CLI)

```powershell
# Проверить статус текущего индекса
cmd /c "set DIGUP_MODELS=D:\_projects\win_search\models&& .\run-vulkan\digup.exe status --index state\index-vulkan"

# Проиндексировать новую папку
cmd /c "set DIGUP_MODELS=D:\_projects\win_search\models&& .\run-vulkan\digup.exe index ""C:\MyFolder"" --index state\index-vulkan"

# Поиск из командной строки
cmd /c "set DIGUP_MODELS=D:\_projects\win_search\models&& .\run-vulkan\digup.exe search ""договор оказания услуг"" --index state\index-vulkan"
```

---

## 🧪 Тестирование и бенчмарки

Решение покрыто модульными и сквозными тестами:

```powershell
# Запуск 33 модульных тестов xUnit
& ".\tools\dotnet\dotnet.exe" test port\DigUp-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f\windows\DigUp.Windows.sln
```

- **Результаты русскоязычного бенчмарка (`results/eval-russian.json`):**
  - Точность **Top-1: 100%** (3/3 exact OCR, 3/3 semantic cross-lingual);
  - Точность **Top-5: 100%**;
  - Время теплого отклика (Vulkan): **23.2 мс**.

---

## 📦 Сборка дистрибутива (Velopack Setup & Portable)

Скрипт автономной упаковки собирает `Setup.exe` и портативный `.zip`:

```powershell
powershell -ExecutionPolicy Bypass -File port\DigUp-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f\windows\scripts\build-package.ps1
```

Готовые пакеты размещаются в каталоге `dist/releases/`:
- `DigUp-win-Setup.exe` — установщик для пользователя (no-admin);
- `DigUp-win-Portable.zip` — автономная портативная версия;
- `SHA256SUMS.txt` — контрольные суммы релизных файлов.

---

## 📄 Лицензия

Проект лицензирован под [MIT License](LICENSE).  

- Исходный код DigUp (macOS): Copyright (c) 2026 Abdur Rahim.
- Реализация Windows-порта: Copyright (c) 2026 win_search contributors.
