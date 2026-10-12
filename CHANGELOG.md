# Changelog: DigUp Windows Port (win_search)

Все заметные изменения проекта фиксируются в этом документе.
Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.0.0/).

---

## [Unreleased]

### Added
- **Многопоточный скоринг векторов (OPT-02):** В `Searcher.Score` добавлено распараллеливание блочного сравнения через `Parallel.For` для чанков матриц `>= 128` строк с переиспользованием SIMD-векторов.
- **Масштабируемый HNSW-индекс (OPT-03):** Реализован прототип многослойного графа `HnswIndex.cs` на базе SIMD `DotProduct` с логарифмической сложностью поиска O(log N) для коллекций > 100 000 векторов. Добавлен набор тестов `HnswTests.cs`.
- **Нативный WIC-декодер изображений (FMT-01):** Реализован `WicImageDecoder.cs` на базе Windows Imaging Component (WinRT `BitmapDecoder`/`BitmapEncoder`), обеспечивающий аппаратный даунскейлинг Fant, автоматический учёт EXIF-ориентации, чтение метаданных съемки и поддержку современных форматов (WebP, HEIC, TIFF, PNG, JPEG).
- **Каскадный OCR и Tesseract fallback (OCR-02):** Реализованы `CompositeOcrEngine.cs` и `TesseractOcrEngine.cs` с автоматическим переключением при сбоях или отсутствии языковых пакетов WinRT. Добавлены тесты делегирования в `OcrTests.cs`.
- **Оптимизация видеоизвлечения FFmpeg (FMT-02):** Оптимизированы аргументы вызова FFmpeg (`-nostdin -threads 0 -an/-vn -sn -dn`) в `AudioVideoExtractor.cs`, устраняющие блокировки потоков и ускоряющие сэмплирование кадров. Добавлен тестовый набор `AudioVideoTests.cs`.
- **Русскоязычный бенчмарк (RU-03):** Создан тестовый манифест `testbed-russian-eval.json` и сгенерирован отчёт `results/eval-russian.json`: точность Top-1 составила 100% (3/3 exact OCR, 3/3 semantic), 0 ложных срабатываний на отрицательных запросах, медианное время отклика **23.2 мс**.
- **Автоматизированная дистрибуция и упаковка (SHIP-01, SHIP-02):**
  - Написан скрипт `windows/scripts/build-package.ps1` для полностью автономной сборки.
  - Установлен и настроен упаковщик Velopack (`vpk`).
  - Собран безадминский per-user установщик `DigUp-win-Setup.exe` (307 МБ) с автосозданием ярлыков и поддержкой фоновых обновлений.
  - Собран переносимый автономный архив `DigUp-win-Portable.zip` (299 МБ).
  - Сгенерирован файл контрольных сумм `dist/releases/SHA256SUMS.txt`.
- **Расширение тестов:** Общее количество автоматизированных модульных тестов в `DigUp.Tests` увеличено до 33 (все 33 теста успешно проходят в `dotnet test`).
- Реализован нативный движок оптического распознавания текста [WindowsMediaOcrEngine](file:///d:/_projects/win_search/port/DigUp-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/src/DigUp.Core/WindowsMediaOcrEngine.cs) на базе `Windows.Media.Ocr.OcrEngine` (WinRT), заменивший неработающую заглушку `WindowsOcrEngineStub`.
- Подключена поддержка распознавания скриншотов на языках профиля пользователя Windows (включая русский и английский) без сторонних тяжелых бинарников.
- Добавлена поддержка русскоязычных префиксов скриншотов Windows («Снимок экрана», «Снимок», «Скриншот») в [Rules.cs](file:///d:/_projects/win_search/port/DigUp-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f/windows/src/DigUp.Core/Rules.cs) с регистронезависимым сопоставлением (`OrdinalIgnoreCase`).
- Добавлен тестовый скриншот `testbed/Скриншот-счет.png` и проверен сквозной сценарий OCR-индексации и полнотекстового поиска по реквизитам счета («88392», «15400»).
- Добавлен аппаратный SIMD-метод скалярного произведения `Vectors.DotProduct` с 4-way unrolling на базе `System.Numerics.Vector<float>` (AVX2 / AVX-512 / Neon).
- Метод `Searcher.Score` переведён на аппаратный SIMD `DotProduct`, устранив скалярный вложенный цикл `for` по 768 float-числам.
- Расширен список стоп-слов `Searcher.Stopwords` для русского языка.
- Создан тестовый проект `DigUp.Tests` (xUnit) и включен в `DigUp.Windows.sln`.
- Запущен смоук-тест `DigUp.Smoke` на бэкенде Vulkan: зафиксирована 100% точность (Top-1 11/11, Top-5 11/11), задержка warm encode + search составила **23.1 мс**.
- Инициализирован приватный репозиторий проекта на GitHub: `git@github.com:ex-ru/win_search.git`.
- Создан файл управления бэклогом [BACKLOG.md](file:///d:/_projects/win_search/BACKLOG.md) с декомпозицией на 6 эпиков.
- Подтверждена работа GPU-ускорения на видеокарте AMD Radeon RX 7900 GRE через Vulkan-рантайм (`ggml-vulkan.dll` + системный `vulkan-1.dll`), зафиксировано время старта модели 1.35 с.

---

## [0.1.0-alpha] - 2026-10-12

### Baseline (PR #18 Windows Port)
- Базовый перенос архитектуры DigUp с macOS (Swift/Metal) на Windows (C# .NET 9 / WPF).
- Процесс-воркер `digup-embed.exe` на базе llama.cpp (b11461) с протоколом JSON-lines для EmbeddingGemma 2 (Q8_0, 768-dim).
- Полнотекстовый и векторный индекс в SQLite (`IndexStore`, WAL, FTS5).
- Консольная утилита `digup.exe` с командами `estimate`, `index`, `search`, `status`.
- Графический интерфейс на WPF (`DigUpApp.exe`): системный трей, глобальный хоткей `Ctrl+Alt+Space`, окно быстрого поиска с затемнением экрана.
