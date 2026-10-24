// Settings: one small JSON file. No registry writes (except the opt-in
// "start with Windows" login item, which is one value under HKCU Run).
// %LocalAppData%\WAI\settings.json

using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WAI.Core;

namespace WAI.App;

public sealed class Settings
{
    public List<string> Folders { get; set; } = [];
    public List<string> Skipped { get; set; } = [];
    public string Hotkey { get; set; } = "ctrl+space";
    public string Language { get; set; } = "ru";
    /// <summary>How dark the screen gets behind search, 0..0.8.</summary>
    public double Dim { get; set; } = 0.35;
    /// <summary>Blur the screen behind search (Windows acrylic).</summary>
    public bool Blur { get; set; } = true;
    private double _widgetOpacity = 1.0;
    /// <summary>Opacity of search widget: 0.5..1.0.</summary>
    public double WidgetOpacity
    {
        get => Math.Clamp(_widgetOpacity, 0.5, 1.0);
        set => _widgetOpacity = Math.Clamp(value, 0.5, 1.0);
    }
    /// <summary>Width of the search window (remembered between sessions), 580..1400.</summary>
    public double WindowWidth { get; set; } = 720;
    /// <summary>Acrylic / blur effect behind search widget.</summary>
    public bool WidgetBlur { get; set; } = false;
    /// <summary>Interface theme: "system", "dark", or "light".</summary>
    public string Theme { get; set; } = "system";
    public bool Code { get; set; }
    public bool CheckUpdates { get; set; } = true;
    public string UpdateSource { get; set; } = Environment.GetEnvironmentVariable("WAI_UPDATE_SOURCE") ?? AppConstants.DefaultUpdateSource;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public string Editor { get; set; } = "auto";
    /// <summary>Hardware acceleration mode: "auto", "gpu_ext", "gpu_int", "cpu".</summary>
    public string HardwareMode { get; set; } = "auto";
    /// <summary>Encode short search queries on CPU (AVX2/AVX-512) for instant response without GPU startup latency.</summary>
    public bool QueryFastPathCpu { get; set; } = true;
    /// <summary>Active embedding model: "gemma2" or "bge-m3".</summary>
    public string EmbeddingModel { get; set; } = "gemma2";
    /// <summary>Display image search results as a thumbnail gallery grid.</summary>
    public bool PreferGallery { get; set; } = false;

    /// <summary>True when there was no settings file: first run, show setup.</summary>
    public bool IsNew { get; set; } = true;
    /// <summary>True once the one-time setup was finished. The engine never
    /// starts a blind giant index before that.</summary>
    public bool SetupDone { get; set; }

    public static string Dir =>
        Environment.GetEnvironmentVariable("WAI_HOME") is { Length: > 0 } home ? home :
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAI");
    public static string LogsDir => System.IO.Path.Combine(Dir, "logs");
    public static string Path => System.IO.Path.Combine(Dir, "settings.json");
    public static string BackupPath => System.IO.Path.Combine(Dir, "settings.json.bak");
    public static string IndexDir =>
        Environment.GetEnvironmentVariable("WAI_INDEX") is { Length: > 0 } d ? d
        : System.IO.Path.Combine(Dir, "index.noindex");

    public static readonly string[] Editors = ["auto", "code", "cursor", "idea64", "pycharm64", "rider64", "webstorm64", "devenv", "notepad++", "subl"];

    private static string? TryReadJsonWithRetry(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                ErrorLog.Write($"Failed to read {filePath} on attempt {i + 1}: {ex.Message}");
                break;
            }
        }
        return null;
    }

    private static Settings? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Settings>(json);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Settings deserialization error: " + ex.Message);
            return null;
        }
    }

    public static Settings Load()
    {
        string? primaryJson = TryReadJsonWithRetry(Path);
        var s = TryDeserialize(primaryJson);
        string? activeJson = primaryJson;

        if (s is null)
        {
            string? backupJson = TryReadJsonWithRetry(BackupPath);
            s = TryDeserialize(backupJson);
            if (s is not null)
            {
                activeJson = backupJson;
                ErrorLog.Write("Recovered settings from backup file.");
            }
        }

        if (s is not null)
        {
            s.IsNew = false;

            // If folders exist in settings, setup was already performed.
            if (s.Folders.Count > 0)
            {
                s.SetupDone = true;
            }
            else if (!string.IsNullOrWhiteSpace(activeJson))
            {
                try
                {
                    using var document = JsonDocument.Parse(activeJson);
                    if (!document.RootElement.TryGetProperty(nameof(SetupDone), out _))
                        s.SetupDone = false;
                }
                catch { s.SetupDone = false; }
            }

            // If folders are empty, attempt recovery from existing SQLite database
            if (s.Folders.Count == 0 && IndexStore.TryRecoverFolders(IndexDir, out var recovered) && recovered.Count > 0)
            {
                s.Folders = recovered;
                s.SetupDone = true;
                s.IsNew = false;
                ErrorLog.Write($"Recovered {recovered.Count} folder(s) from existing index: {string.Join(", ", recovered)}");
                try { s.Save(); } catch { }
            }

            // Migrate to new default Ctrl+Space if old default was present
            if (s.Hotkey is "shift+alt+space" or "ctrl+alt+space")
                s.Hotkey = "ctrl+space";
            if (string.IsNullOrWhiteSpace(s.UpdateSource) ||
                string.Equals(s.UpdateSource, "dist\\releases", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.UpdateSource, "dist/releases", StringComparison.OrdinalIgnoreCase))
            {
                s.UpdateSource = AppConstants.DefaultUpdateSource;
            }
            if (s.WindowWidth < 580)
                s.WindowWidth = 720;
            // Keep settings from older releases usable when a model is no longer supported.
            s.EmbeddingModel = ModelCatalog.Get(s.EmbeddingModel).Id;
            return s;
        }

        var fresh = new Settings { IsNew = true };
        if (IndexStore.TryRecoverFolders(IndexDir, out var recoveredFromDb) && recoveredFromDb.Count > 0)
        {
            fresh.Folders = recoveredFromDb;
            fresh.SetupDone = true;
            fresh.IsNew = false;
            ErrorLog.Write($"Recovered {recoveredFromDb.Count} folder(s) into fresh settings from index: {string.Join(", ", recoveredFromDb)}");
            try { fresh.Save(); } catch { }
            return fresh;
        }

        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (Directory.Exists(docs)) fresh.Folders.Add(docs);
        return fresh;
    }

    public void Save()
    {
        IsNew = false;
        if (Folders.Count > 0)
        {
            SetupDone = true;
        }
        Directory.CreateDirectory(Dir);
        // Atomic: a kill mid-write leaves the old file, never half a file.
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(Path))
        {
            try { File.Copy(Path, BackupPath, overwrite: true); } catch { }
        }
        File.Move(tmp, Path, overwrite: true);
    }

    private const string RunRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    // "Start with Windows": one value under HKCU ...\Run (this user only,
    // no admin), synchronized with StartupApproved\Run so Windows 10/11 does not block it.
    public bool StartWithWindows
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
                bool hasEntry = run?.GetValue(AppConstants.AppName) is not null || run?.GetValue("DigUp") is not null;
                if (!hasEntry) return false;

                using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedRegistryKey, false);
                if (approved is not null)
                {
                    if (approved.GetValue(AppConstants.AppName) is byte[] b && b.Length > 0 && (b[0] & 1) != 0)
                        return false;
                    if (approved.GetValue("DigUp") is byte[] bOld && bOld.Length > 0 && (bOld[0] & 1) != 0)
                        return false;
                }
                return true;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunRegistryKey);
                if (run is null) return;
                if (value)
                {
                    string? exePath = Environment.ProcessPath;
                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        exePath = Environment.GetCommandLineArgs().FirstOrDefault();
                    }
                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        exePath = AppDomain.CurrentDomain.BaseDirectory;
                    }
                    if (!string.IsNullOrWhiteSpace(exePath))
                    {
                        try { exePath = System.IO.Path.GetFullPath(exePath); } catch { }
                        run.SetValue(AppConstants.AppName, $"\"{exePath}\" --silent");
                        try { run.DeleteValue("DigUp", false); } catch { }

                        // Ensure Windows Explorer / Task Manager does not block it in StartupApproved\Run
                        try
                        {
                            using var approved = Registry.CurrentUser.CreateSubKey(StartupApprovedRegistryKey);
                            if (approved is not null)
                            {
                                if (approved.GetValue(AppConstants.AppName) is byte[] b && b.Length >= 1)
                                {
                                    b[0] = 0x02; // 0x02 = Enabled in Windows
                                    approved.SetValue(AppConstants.AppName, b, RegistryValueKind.Binary);
                                }
                                else
                                {
                                    byte[] enabled = new byte[12];
                                    enabled[0] = 0x02;
                                    approved.SetValue(AppConstants.AppName, enabled, RegistryValueKind.Binary);
                                }
                                try { approved.DeleteValue("DigUp", false); } catch { }
                            }
                        }
                        catch { }
                    }
                }
                else
                {
                    try { run.DeleteValue(AppConstants.AppName, false); } catch { }
                    try { run.DeleteValue("DigUp", false); } catch { }
                    try
                    {
                        using var approved = Registry.CurrentUser.CreateSubKey(StartupApprovedRegistryKey);
                        approved?.DeleteValue(AppConstants.AppName, false);
                        approved?.DeleteValue("DigUp", false);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Synchronizes StartupApproved registry state when autostart is configured,
    /// healing any stale disabled flags (e.g. byte 0 == 3) that prevent Windows from running WAI on boot.
    /// </summary>
    public static void SyncStartupApproved()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            bool hasEntry = run?.GetValue(AppConstants.AppName) is not null || run?.GetValue("DigUp") is not null;
            if (!hasEntry) return;

            using var approved = Registry.CurrentUser.CreateSubKey(StartupApprovedRegistryKey);
            if (approved is null) return;

            if (approved.GetValue(AppConstants.AppName) is byte[] b && b.Length >= 1)
            {
                if ((b[0] & 1) != 0)
                {
                    b[0] = 0x02;
                    approved.SetValue(AppConstants.AppName, b, RegistryValueKind.Binary);
                }
            }
            else if (approved.GetValue(AppConstants.AppName) is null)
            {
                byte[] enabled = new byte[12];
                enabled[0] = 0x02;
                approved.SetValue(AppConstants.AppName, enabled, RegistryValueKind.Binary);
            }
            try { approved.DeleteValue("DigUp", false); } catch { }
        }
        catch { }
    }
}
