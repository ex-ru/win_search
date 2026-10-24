using Microsoft.Win32;
using WAI.App;
using Xunit;

namespace WAI.Tests;

public sealed class StartupSettingsTests
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

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
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, false))
            {
                var val = run?.GetValue(AppConstants.AppName) as string;
                Assert.NotNull(val);
                Assert.Contains("--silent", val);
            }

            // Assert: StartupApproved contains enabled flag (byte 0 is even / 0x02)
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, false))
            {
                var bytes = approved?.GetValue(AppConstants.AppName) as byte[];
                Assert.NotNull(bytes);
                Assert.True(bytes.Length > 0);
                Assert.Equal(0, bytes[0] & 1); // Not odd -> enabled
            }

            // Act: set to false
            settings.StartWithWindows = false;

            // Assert: getter returns false and registry value is removed
            Assert.False(settings.StartWithWindows);
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, false))
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

    [Fact]
    public void StartWithWindows_HealsStartupApproved_WhenPreviouslyDisabled()
    {
        var settings = new Settings();
        bool initial = settings.StartWithWindows;

        try
        {
            // Simulate Windows or Task Manager marking WAI as disabled (0x03)
            using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey))
            {
                approved.SetValue(AppConstants.AppName, new byte[] { 3, 0, 0, 0, 20, 50, 139, 12, 13, 99, 221, 1 }, RegistryValueKind.Binary);
            }

            // Act: user sets StartWithWindows to true in Settings
            settings.StartWithWindows = true;

            // Assert: byte 0 must be updated to 0x02 (Enabled)
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, false))
            {
                var bytes = approved?.GetValue(AppConstants.AppName) as byte[];
                Assert.NotNull(bytes);
                Assert.Equal(0x02, bytes[0]);
            }
            Assert.True(settings.StartWithWindows);
        }
        finally
        {
            settings.StartWithWindows = initial;
        }
    }

    [Fact]
    public void StartWithWindows_GetterReturnsFalse_WhenStartupApprovedDisabled()
    {
        var settings = new Settings();
        bool initial = settings.StartWithWindows;

        try
        {
            // Put command in Run, but mark as disabled (0x03) in StartupApproved
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                run.SetValue(AppConstants.AppName, "\"C:\\fake\\WAIApp.exe\" --silent");
            }
            using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey))
            {
                approved.SetValue(AppConstants.AppName, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            }

            // Assert: Getter reflects that Windows Explorer won't run it
            Assert.False(settings.StartWithWindows);

            // Act: SyncStartupApproved heals the flag
            Settings.SyncStartupApproved();

            // Assert: after sync, byte 0 is 0x02 and getter returns true
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, false))
            {
                var bytes = approved?.GetValue(AppConstants.AppName) as byte[];
                Assert.NotNull(bytes);
                Assert.Equal(0x02, bytes[0]);
            }
            Assert.True(settings.StartWithWindows);
        }
        finally
        {
            settings.StartWithWindows = initial;
        }
    }
}
