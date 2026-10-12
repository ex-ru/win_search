# Changelog: DigUp Windows Port (win_search)

Все заметные изменения проекта фиксируются в этом документе.
Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.0.0/).

---

## [Unreleased]

### Added
- Добавлен аппаратный SIMD-метод скалярного произведения `Vectors.DotProduct` с 4-way unrolling на базе `System.Numerics.Vector<float>` (AVX2 / AVX-512 / Neon).
- Метод `Searcher.Score` переведён на аппаратный SIMD `DotProduct`, устранив скалярный вложенный цикл `for` по 768 float-числам.
- Расширен список стоп-слов `Searcher.Stopwords` для русского языка (союзы, предлоги, частицы, мусорные слова и служебные маркеры поиска: «найди», «покажи», «файл», «скриншот» и др.).
- Создан тестовый проект `DigUp.Tests` (xUnit) и включен в `DigUp.Windows.sln`.
- Написаны 7 модульных тестов, проверяющих:
  - Точность и совпадение SIMD `DotProduct` с эталонным скалярным расчётом;
  - Ортогональность и единичную норму;
  - Корректность упаковки и распаковки float16 `ToHalfBlob`/`FromHalfBlob`;
  - Санитизацию повреждённых суррогатных пар Unicode (lone surrogates);
  - Фильтрацию русских стоп-слов и извлечение ключевых слов из русскоязычных и смешанных запросов.
- Запущен смоук-тест `DigUp.Smoke` на бэкенде Vulkan: зафиксирована 100% точность (Top-1 11/11, Top-5 11/11), задержка warm encode + search составила **23.1 мс**.
- Инициализирован приватный репозиторий проекта на GitHub: `git@github.com:ex-ru/win_search.git`.
- Создан файл управления бэклогом [BACKLOG.md](file:///d:/_projects/win_search/BACKLOG.md) с декомпозицией на 6 эпиков (SIMD-оптимизация, OCR, русский язык, unit-тесты, медиаформаты, Velopack).
- Настроена и проверена конфигурация сборки с локальным .NET 9 SDK (9.0.318) и автономным кэшем пакетов NuGet.
- Подтверждена работа GPU-ускорения на видеокарте AMD Radeon RX 7900 GRE через Vulkan-рантайм (`ggml-vulkan.dll` + системный `vulkan-1.dll`), зафиксировано время старта модели 1.35 с.

---

## [0.1.0-alpha] - 2026-10-12

### Baseline (PR #18 Windows Port)
- Базовый перенос архитектуры DigUp с macOS (Swift/Metal) на Windows (C# .NET 9 / WPF).
- Процесс-воркер `digup-embed.exe` на базе llama.cpp (b11461) с протоколом JSON-lines для EmbeddingGemma 2 (Q8_0, 768-dim).
- Полнотекстовый и векторный индекс в SQLite (`IndexStore`, WAL, FTS5).
- Консольная утилита `digup.exe` с командами `estimate`, `index`, `search`, `status`.
- Графический интерфейс на WPF (`DigUpApp.exe`): системный трей, глобальный хоткей `Ctrl+Alt+Space`, окно быстрого поиска с затемнением экрана.
