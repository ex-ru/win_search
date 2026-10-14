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
        ? "Выберите модель машинного обучения для семантического поиска. Поддерживается переключение между EmbeddingGemma 2, BGE-M3, Perplexity Embed v2 и Nomic v1.5 с сохранением поиска по всем типам файлов."
        : "Select machine learning model for semantic search. Switch between EmbeddingGemma 2, BGE-M3, Perplexity Embed v2 and Nomic v1.5 with support for all file types preserved.";
    public static string ModelActiveLabel => IsRu ? "Активная модель эмбеддингов:" : "Active embedding model:";
    public static string ModelStatusHeader => IsRu ? "🧠 Характеристики и статус модели:" : "🧠 Model specifications and status:";
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
    public static string UpdateSourceLabel => IsRu ? "Источник пакетов обновлений (GitHub Releases или локальная папка):" : "Update package source (GitHub Releases or local folder):";
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
}
