using Microsoft.Win32;

namespace WAI.App;

public static class ThemeManager
{
    public const string ThemeSystem = "system";
    public const string ThemeDark = "dark";
    public const string ThemeLight = "light";

    public static readonly string[] AvailableThemes = [ThemeSystem, ThemeDark, ThemeLight];

    /// <summary>
    /// Checks Windows Personalize registry key to see if Windows apps use dark theme.
    /// </summary>
    public static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val)
            {
                return val == 0;
            }
        }
        catch { }
        return true; // Default to dark theme for WAI
    }

    /// <summary>
    /// Returns true if effective theme should be dark based on user settings and Windows theme.
    /// </summary>
    public static bool IsDark(string? theme)
    {
        if (string.Equals(theme, ThemeLight, StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(theme, ThemeDark, StringComparison.OrdinalIgnoreCase))
            return true;
        return IsSystemDarkTheme();
    }
}
