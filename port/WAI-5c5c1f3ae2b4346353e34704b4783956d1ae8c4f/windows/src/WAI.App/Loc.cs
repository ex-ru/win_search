namespace WAI.App;

public static class Loc
{
    public static string CurrentLanguage { get; set; } = "ru";

    public static bool IsRu => CurrentLanguage.Equals("ru", StringComparison.OrdinalIgnoreCase);

    public static string TrayReadyTitle => IsRu ? "WAI готов к работе" : "WAI is ready";
    public static string TrayReadyText(string hotkey) => IsRu
        ? $"Поиск: {hotkey} (или клик по иконке в трее)"
        : $"Search: {hotkey} (or click tray icon)";
    public static string AppSubtitle => IsRu ? "поиск по смыслу файлов" : "semantic file search";

    public static string OpenSearch => IsRu ? "Открыть поиск" : "Open search";
    public static string Settings => IsRu ? "Настройки…" : "Settings…";
    public static string About => IsRu ? "О программе WAI" : "About WAI";
    public static string Quit => IsRu ? "Выход (WAI)" : "Quit WAI";

    public static string IndexDone(int count) => IsRu
        ? $"Выполнено: проиндексировано {count} файлов"
        : $"Done: indexed {count} files";

    public static string IndexRunning(int done, int remaining) => IsRu
        ? $"Индексация: {done} файлов, осталось {remaining}"
        : $"Indexing: {done} files ({remaining} remaining)";

    public static string IndexPaused(int count) => IsRu
        ? $"Пауза: проиндексировано {count} файлов"
        : $"Paused: indexed {count} files";

    public static string Indexing => IsRu ? "Индексация" : "Indexing";
    public static string VersionPrefix(string v) => IsRu ? $"Версия: v{v}" : $"Version: v{v}";
    public static string SearchTooltip => IsRu
        ? "Опишите, что ищете. code: для поиска кода. Enter — открыть, Esc — скрыть."
        : "Describe what you're looking for. code: to search code. Enter to open, Esc to hide.";
    public static string ClearQueryTooltip => IsRu ? "Очистить строку поиска" : "Clear search query";

    public static string SearchPlaceholder => IsRu
        ? "Поиск по смыслу и словам… (например: отчёт за май, договор, схема архитектуры)"
        : "Search by meaning and keywords… (e.g. quarterly report, contract, architecture)";

    public static string HintInitialIndex => IsRu
        ? "Первичная индексация может занять некоторое время. Вы можете закрыть окно — процесс автоматически продолжится при следующем запуске."
        : "Initial indexing may take some time. You can close the app — indexing will automatically resume on next launch.";

    public static string Open => IsRu ? "Открыть" : "Open";
    public static string InExplorer => IsRu ? "В проводнике" : "In Explorer";
    public static string OpenWith => IsRu ? "Выбрать программу…" : "Open with…";
    public static string SaveAs => IsRu ? "Сохранить как…" : "Save as…";
    public static string CopyPath => IsRu ? "Копировать путь" : "Copy path";
    public static string Close => IsRu ? "Закрыть" : "Close";
    public static string Minimize => IsRu ? "Свернуть" : "Minimize";
    public static string Apply => IsRu ? "Применить" : "Apply";
    public static string Cancel => IsRu ? "Отмена" : "Cancel";
    public static string Save => IsRu ? "Сохранить" : "Save";

    // Settings Window Strings
    public static string SettingsTitle => IsRu ? "WAI — Настройки" : "WAI — Settings";
    public static string SettingsHeader => IsRu ? "Настройки WAI" : "WAI Settings";

    public static string BannerInitialIndex => IsRu
        ? "Первичная индексация может занять некоторое время. Вы можете свернуть или закрыть приложение — при повторном открытии процесс автоматически продолжится с того же места."
        : "Initial indexing may take some time. You can minimize or close the app — indexing will automatically resume from the same place on next launch.";

    // Folders
    public static string FoldersTitle => IsRu ? "Папки для поиска" : "Folders to search";
    public static string FoldersDesc => IsRu
        ? "Файлы в этих папках индексируются для мгновенного семантического и текстового поиска."
        : "Files in these folders are indexed for instant semantic and keyword search.";
    public static string AddFolder => IsRu ? "Добавить папку…" : "Add folder…";
    public static string RemoveFolder => IsRu ? "Удалить" : "Remove";
    public static string StatsHeader => IsRu ? "📊 Статистика эмбеддингов:" : "📊 Embeddings statistics:";

    // Orphaned Vectors
    public static string OrphanedTitle => IsRu ? "Сохраненные векторы неактивных / удаленных папок" : "Cached vectors of inactive / removed folders";
    public static string OrphanedInfo(int files, int vectors, string size) => IsRu
        ? $"В базе сохранено: {files} файлов ({vectors} векторов, {size}). Векторы сохранены и ускорят поиск при повторном добавлении папок."
        : $"Preserved in index: {files} files ({vectors} vectors, {size}). Kept to speed up indexing if folders are re-added.";
    public static string PurgeOrphanedBtn => IsRu ? "🗑️ Очистить кэш удаленных папок" : "🗑️ Clear removed folders cache";
    public static string PurgeConfirmTitle => IsRu ? "Очистка кэша неактивных папок" : "Purge inactive folder cache";
    public static string PurgeConfirmText(int files, string size) => IsRu
        ? $"Вы действительно хотите удалить сохраненные векторы неактивных папок ({files} файлов, {size})?\nПри повторном добавлении этих папок потребуется повторная индексация."
        : $"Are you sure you want to delete cached vectors for inactive folders ({files} files, {size})?\nIf re-added later, they will need to be re-indexed.";

    // Logs
    public static string LogsTitle => IsRu ? "Диагностические логи" : "Diagnostic logs";
    public static string LogsDesc => IsRu
        ? "Логи работы WAI, GPU/Vulkan и фонового процесса индексации хранятся локально в папке логов."
        : "Operational logs for WAI, GPU/Vulkan, and background indexing are stored in the logs folder.";
    public static string OpenLogsBtn => IsRu ? "📂 Открыть папку с логами" : "📂 Open logs folder";

    // Vectors
    public static string VectorsTitle => IsRu ? "Векторный индекс и эмбеддинги" : "Vector index and embeddings";
    public static string VectorsDesc => IsRu
        ? "Векторные эмбеддинги формируются локальной моделью и обеспечивают поиск по смыслу запроса."
        : "Vector embeddings are generated by the local AI model to provide semantic meaning-based search.";
    public static string ClearFolderVectors => IsRu ? "Очистить векторы выбранной папки" : "Clear selected folder vectors";
    public static string ClearAllVectors => IsRu ? "Очистить весь векторный индекс" : "Clear entire vector index";
    public static string VectorsNote => IsRu
        ? "Используйте очистку выбранной папки для частичного обновления или полную очистку при смене модели эмбеддингов."
        : "Use clearing a selected folder for partial updates, or clear all when changing models.";

    // Embedding Model
    public static string ModelTitle => IsRu ? "Модель векторных эмбеддингов (Нейросеть поиска)" : "Embedding Model (Search Neural Network)";
    public static string ModelDesc => IsRu
        ? "Выберите одну из трёх моделей для поиска по смыслу: Gemma ищет по тексту, изображениям и аудио; BGE-M3 и Nomic — по тексту, включая распознанный OCR текст на изображениях."
        : "Choose one of three semantic search models: Gemma searches text, images, and audio; BGE-M3 and Nomic search text, including OCR text recognized in images.";
    public static string ModelActiveLabel => IsRu ? "Активная модель эмбеддингов:" : "Active embedding model:";
    public static string ModelStatusHeader => IsRu ? "🧠 Характеристики и статус модели:" : "🧠 Model specifications and status:";
    public static string ModelDownloadBtn => IsRu ? "📥 Скачать веса модели" : "📥 Download model weights";
    public static string ModelDownloadingDefault => IsRu ? "Загрузка модели…" : "Downloading model…";
    public static string ModelDownloading(string name) => IsRu ? $"Загрузка {name}…" : $"Downloading {name}…";
    public static string ModelCancelDownload => IsRu ? "Отмена" : "Cancel";
    public static string ModelDownloadCompleteTitle => IsRu ? "Загрузка завершена" : "Download Complete";
    public static string ModelDownloadComplete(string name) => IsRu
        ? $"Модель '{name}' успешно загружена и готова к работе."
        : $"Model '{name}' downloaded successfully and is ready to use.";
    public static string ModelDownloadErrorTitle => IsRu ? "Ошибка загрузки" : "Download Error";
    public static string ModelNote => IsRu
        ? "Для каждой модели создаётся отдельный изолированный индекс. Предыдущие индексы сохраняются и не удаляются при переключении."
        : "Each model uses an isolated index database. Previous indices are preserved and not deleted when switching.";

    // Hardware
    public static string HwTitle => IsRu ? "Аппаратное ускорение (CPU / GPU для эмбеддингов)" : "Hardware Acceleration (CPU / GPU for embeddings)";
    public static string HwDesc => IsRu
        ? "Выбор устройства для локальной генерации векторных эмбеддингов (поиск по смыслу). Поддерживаются дискретные и встроенные видеокарты всех производителей и процессорные вычисления."
        : "Select compute device for local vector embeddings generation. Supports discrete & integrated GPUs from all vendors and CPU calculations.";
    public static string HwDeviceLabel => IsRu ? "Вычислительное устройство:" : "Compute device:";
    public static string HwStatusHeader => IsRu ? "🖥 Обнаруженное оборудование:" : "🖥 Detected hardware:";
    public static string HwNote => IsRu
        ? "Поддерживаются дискретные NVIDIA (CUDA/Vulkan), AMD Radeon (Vulkan), Intel Arc (Vulkan), встроенные Intel Iris Xe/UHD и AMD Radeon APU. В случае сбоя GPU происходит безопасный откат на CPU."
        : "Supports discrete NVIDIA (CUDA/Vulkan), AMD Radeon (Vulkan), Intel Arc (Vulkan), Intel Iris Xe/UHD and AMD Radeon APU. Safely falls back to CPU on GPU error.";
    public static string HwAuto(string summary) => IsRu ? $"Автоопределение (Рекомендуется — {summary})" : $"Auto-detect (Recommended — {summary})";
    public static string HwDiscrete(string name, ulong vram) => IsRu ? $"Дискретная видеокарта: {name} ({vram} МБ VRAM)" : $"Discrete GPU: {name} ({vram} MB VRAM)";
    public static string HwIntegrated(string name) => IsRu ? $"Встроенная графика: {name}" : $"Integrated GPU: {name}";
    public static string HwCpu(int cores) => IsRu ? $"Только процессор (CPU, {cores} потоков)" : $"CPU only ({cores} threads)";
    public static string HwQueryFastPath => IsRu ? "Мгновенный старт поиска на CPU (Query Fast-Path)" : "Instant search start on CPU (Query Fast-Path)";
    public static string HwQueryFastPathTooltip => IsRu
        ? "Короткие поисковые запросы кодируются на CPU (AVX2/AVX-512) за ~10-15 мс без задержек инициализации контекста GPU/Vulkan. GPU сохраняется для пакетной фоновой индексации."
        : "Short search queries are encoded on CPU (AVX2/AVX-512) in ~10-15 ms without GPU/Vulkan initialization latency. GPU is preserved for batch background indexing.";
    public static string SearchingMeaning => IsRu ? "Ищем по смыслу…" : "Searching by meaning…";
    public static string LexicalHitsSuffix(int count) => IsRu ? $"{count} совпадений (текст) · Ищем по смыслу…" : $"{count} keyword matches · Searching by meaning…";
    public static string FoundResults(int count) => IsRu ? $"Найдено результатов: {count}" : $"Found {count} results";
    public static string NoResultsFound => IsRu ? "Ничего не найдено" : "No results found";

    // Appearance
    public static string AppearanceTitle => IsRu ? "Внешний вид и прозрачность виджета поиска" : "Appearance and Search Widget Translucency";
    public static string AppearanceDesc => IsRu
        ? "Настройка полупрозрачности и темы для виджета поиска."
        : "Configure translucency and theme for the search widget.";
    public static string OpacityLabel => IsRu ? "Прозрачность виджета поиска:" : "Search widget opacity:";
    public static string WidgetBlur => IsRu ? "Эффект размытия фона под виджетом (Acrylic Blur)" : "Background acrylic blur effect behind search widget";
    public static string ThemeLabel => IsRu ? "Тема оформления интерфейса:" : "Interface theme:";

    // Hotkey
    public static string HotkeyTitle => IsRu ? "Горячая клавиша (быстрый вызов)" : "Global hotkey (quick call)";
    public static string HotkeyDesc => IsRu
        ? "По умолчанию: Ctrl+Space. Избегайте Win+Space (системная смена языка) и Shift+Alt (переключение раскладки)."
        : "Default: Ctrl+Space. Avoid Win+Space (system language switch) and Shift+Alt (input layout switch).";

    // Code
    public static string CodeTitle => IsRu ? "Поиск по коду" : "Code search";
    public static string CodeIndexCheck => IsRu ? "Индексировать исходный код (запросы, начинающиеся с code:)" : "Index source code (queries starting with code:)";
    public static string CodeEditorLabel => IsRu ? "Открывать код в редакторе:" : "Open code in editor:";

    // System & Updates
    public static string SystemTitle => IsRu ? "Система и обновления" : "System & Updates";
    public static string StartupCheck => IsRu ? "Запускать WAI при старте Windows" : "Launch WAI on Windows startup";
    public static string UpdatesCheck => IsRu ? "Проверять обновления программы раз в день" : "Check for app updates daily";
    public static string UpdateSourceLabel => IsRu ? "Источник пакетов обновлений (GitHub Releases или папка):" : "Update package source (GitHub Releases or folder):";
    public static string UpdateSourceTooltip => IsRu
        ? "Репозиторий GitHub (по умолчанию: https://github.com/ex-ru/win_search) или локальная папка с релизами."
        : "GitHub repository (default: https://github.com/ex-ru/win_search) or local releases folder.";
    public static string SystemNote => IsRu
        ? "WAI работает per-user: не требует прав администратора, данные хранятся в %LocalAppData%\\WAI."
        : "WAI runs per-user without administrator privileges, data stored in %LocalAppData%\\WAI.";

    // Filter Buttons in MainWindow
    public static string FilterAll => IsRu ? "Все" : "All";
    public static string FilterPdf => IsRu ? "📄 PDF" : "📄 PDF";
    public static string FilterImg => IsRu ? "🖼️ Картинки" : "🖼️ Images";
    public static string FilterDoc => IsRu ? "📝 Текст / Док" : "📝 Text / Docs";
    public static string FilterCode => IsRu ? "💻 Код" : "💻 Code";
    public static string FilterMedia => IsRu ? "🎬 Медиа" : "🎬 Media";

    // Mascot
    public static string MascotSubtitle => IsRu ? "Windows AI Search — быстрый поиск по смыслу" : "Windows AI Search — semantic file search";
    public static string Advanced => IsRu ? "Дополнительно" : "Advanced";
    public static string Searching => IsRu ? "Поиск…" : "Searching…";
    public static string SearchResults(int count, int limit) => count == 0
        ? (IsRu ? "Совпадений нет. Попробуйте другие слова или тип файла." : "No matches. Try different words or file types.")
        : (IsRu ? $"Показано результатов: {count}" : $"Results shown: {count}")
          + (count >= limit ? (IsRu ? ". Уточните запрос, чтобы найти больше." : ". Refine the query to find more.") : "");
    public static string SearchFailed => IsRu ? "Не удалось выполнить поиск. Попробуйте ещё раз; подробности в журнале." : "Search failed. Try again; details are in the log.";
    public static string PreviewFailed => IsRu ? "Предпросмотр недоступен. Файл можно открыть отдельно." : "Preview unavailable. You can open the file separately.";
    public static string FileActionFailed => IsRu ? "Не удалось выполнить действие с файлом. Подробности в журнале." : "The file action failed. Details are in the log.";
    public static string DownloadFailed => IsRu ? "Не удалось загрузить модель. Проверьте подключение и свободное место, затем повторите." : "Could not download the model. Check your connection and free disk space, then retry.";
    public static string Retry => IsRu ? "Повторить" : "Retry";
    public static string SetupTitle => IsRu ? "Настройка WAI" : "WAI setup";
    public static string SetupHeading => IsRu ? "Найдите свои файлы" : "Find your files";
    public static string SetupStep(int step) => IsRu ? $"Шаг {step} из 3" : $"Step {step} of 3";
    public static string SetupFolders => IsRu ? "Выберите папки для поиска. Файлы обрабатываются только на вашем компьютере." : "Choose folders to search. Files are processed only on your computer.";
    public static string SetupPickFolder => IsRu ? "Сначала добавьте хотя бы одну папку." : "Add at least one folder first.";
    public static string SetupDownload => IsRu ? "Загрузим модель поиска. После загрузки поиск работает без интернета." : "Download the search model. After downloading, search works offline.";
    public static string SetupReady => IsRu ? "Всё готово. WAI обработает ваши файлы в фоне; результаты будут появляться постепенно." : "Ready. WAI will read your files in the background; results appear as files are processed.";
    public static string SetupShortcut(string hotkey) => IsRu ? $"Поиск: {hotkey}. Папки и клавиши можно изменить в настройках через значок WAI в трее." : $"Search: {hotkey}. Change folders and shortcuts in Settings through the WAI tray icon.";
    public static string Next => IsRu ? "Далее" : "Next";
    public static string Back => IsRu ? "Назад" : "Back";
    public static string ModelReady => IsRu ? "Модель загружена и проверена." : "Model downloaded and verified.";
    public static string DownloadStopped => IsRu ? "Загрузка остановлена. Можно повторить." : "Download stopped. You can retry.";
    public static string EstimateRunning => IsRu ? "Подсчёт файлов…" : "Counting files…";
    public static string SetupEstimate(int count) => IsRu ? $"Найдено файлов: {count}." : $"Files found: {count}.";
    public static string NoReadableFiles => IsRu ? "Подходящих файлов пока нет. Можно добавить их позже." : "No supported files yet. You can add them later.";
    public static string SemanticUnavailable => IsRu ? "Пока показаны совпадения по словам. Поиск по смыслу недоступен." : "Showing keyword matches. Semantic search is unavailable.";
    public static string Pin => IsRu ? "Закрепить поверх всех окон" : "Keep above other windows";
    public static string Unpin => IsRu ? "Открепить от верха" : "Stop keeping above other windows";
    public static string NoExcerpt => IsRu ? "Нет текстового фрагмента — найдено по смыслу или изображению" : "No text excerpt — matched by meaning or image";
    public static string Video => IsRu ? "Видео" : "Video";
    public static string Audio => IsRu ? "Аудио" : "Audio";
    public static string Page => IsRu ? "страница" : "page";
    public static string CodeLabel => IsRu ? "Код" : "Code";
    public static string PreviewLabel => IsRu ? "Предпросмотр" : "Preview";
    public static string HideSearch => IsRu ? "Скрыть" : "Hide";
}
