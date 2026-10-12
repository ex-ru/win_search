namespace DigUp.App;

public static class Loc
{
    public static string CurrentLanguage { get; set; } = "ru";

    public static bool IsRu => CurrentLanguage.Equals("ru", StringComparison.OrdinalIgnoreCase);

    public static string TrayReadyTitle => IsRu ? "DigUp готов к работе" : "DigUp is ready";
    public static string TrayReadyText(string hotkey) => IsRu
        ? $"Поиск: {hotkey} (или клик по иконке в трее)"
        : $"Search: {hotkey} (or click the tray icon)";

    public static string OpenSearch => IsRu ? "Открыть поиск" : "Open search";
    public static string Settings => IsRu ? "Настройки…" : "Settings…";
    public static string About => IsRu ? "О программе (About)" : "About DigUp";
    public static string Quit => IsRu ? "Выход (Quit DigUp)" : "Quit DigUp";

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
    public static string OpenWith => IsRu ? "Открыть с помощью…" : "Open with…";
    public static string SaveAs => IsRu ? "Сохранить как…" : "Save as…";
    public static string CopyPath => IsRu ? "Копировать путь" : "Copy path";
    public static string Close => IsRu ? "Закрыть" : "Close";
    public static string Minimize => IsRu ? "Свернуть" : "Minimize";
}
