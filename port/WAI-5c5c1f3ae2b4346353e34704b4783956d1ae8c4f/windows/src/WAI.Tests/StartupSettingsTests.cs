using Microsoft.Win32;
using WAI.App;
using Xunit;

namespace WAI.Tests;

public sealed class StartupSettingsTests
{
    [Fact]
    public void StartWithWindows_SetTrue_SetsRegistryAndGetterReturnsTrue()
    {
        var settings = new Settings();
        bool initial = settings.StartWithWindows;

        try
        {
            // Act: set to true
            settings.StartWithWindows = true;

            // Assert: getter returns true and registry value exists
            Assert.True(settings.StartWithWindows);
            using (var run = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
            {
                var val = run?.GetValue(AppConstants.AppName) as string;
                Assert.NotNull(val);
                Assert.Contains("--silent", val);
            }

            // Act: set to false
            settings.StartWithWindows = false;

            // Assert: getter returns false and registry value is removed
            Assert.False(settings.StartWithWindows);
            using (var run = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
            {
                var val = run?.GetValue(AppConstants.AppName);
                Assert.Null(val);
            }
        }
        finally
        {
            // Restore initial state to avoid polluting developer machine
            settings.StartWithWindows = initial;
        }
    }
}
