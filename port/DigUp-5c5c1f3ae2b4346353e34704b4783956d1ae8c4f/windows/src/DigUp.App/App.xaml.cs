// App start: one copy only, tray icon, global hotkey, hidden search panel.
// First run opens setup (folders, code, model download); later runs go
// straight to the tray. No admin. No services. Settings + index live in
// %LocalAppData%\DigUp.

using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using DigUp.Core;
using Velopack;
using WinForms = System.Windows.Forms;

namespace DigUp.App;

public partial class App : System.Windows.Application
{
    [STAThread]
    public static void Main()
    {
        try
        {
            // Installer hooks must run before WPF, the single-instance mutex and indexing.
            VelopackApp.Build().Run();
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Main crash: " + ex);
        }
    }

    private static Mutex? _single;
    private static EventWaitHandle? _showEvent;
    private static RegisteredWaitHandle? _showWaitRegistered;
    private WinForms.NotifyIcon? _tray;
    private MainWindow? _panel;
    private Engine? _engine;
    private SettingsWindow? _settingsWindow;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
    private Hotkey? _hotkey;
    private FileSystemWatcherRunner? _watcher;
    private Settings? _settings;
    private UpdateService? _updates;
    private bool _hotkeyHooked;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ErrorLog.Write($"=== WAI v{AppConstants.Version} Starting (PID={Environment.ProcessId}, OS={Environment.OSVersion}, 64bit={Environment.Is64BitProcess}) ===");
        ErrorLog.Write($"Logs directory: {Settings.LogsDir}");

        // No silent deaths: anything escaping to the top is logged and shown.
        DispatcherUnhandledException += (_, ex) =>
        {
            ErrorLog.WriteException("ui unhandled", ex.Exception);
            ShowNote("Что-то пошло не так (" + FirstLine(ex.Exception.Message) + "). WAI продолжает работу.");
            ex.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            ErrorLog.WriteException("task unobserved", ex.Exception);
            ex.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception exc)
                ErrorLog.WriteException("fatal appdomain", exc);
            else
                ErrorLog.Write("fatal appdomain: " + ex.ExceptionObject);
        };

        _single = new Mutex(true, @"Local\DigUp-Windows-v1", out bool first);
        if (!first)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(@"Local\DigUp-Show-Event", out var existingHandle))
                {
                    existingHandle.Set();
                    existingHandle.Dispose();
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Write("signal existing instance failed: " + ex.Message);
            }
            Shutdown();
            return;
        }

        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DigUp-Show-Event");
            _showWaitRegistered = ThreadPool.RegisterWaitForSingleObject(
                _showEvent,
                (_, _) =>
                {
                    Dispatcher.BeginInvoke(() => ShowSearchPanel());
                },
                null,
                -1,
                false);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("show-event init: " + ex.Message);
        }

        _settings = Settings.Load();
        _engine = new Engine(_settings);
        _panel = new MainWindow(_engine, _settings);
        _panel.Hide();

        MakeTray();
        _updates = new UpdateService(_settings, ShowNote);
        _updates.Start();
        RegisterHotkey();
        _watcher = new FileSystemWatcherRunner(_settings, () => _engine.RequestIndex());

        if (_settings.IsNew || !_settings.SetupDone)
        {
            var setup = new Onboarding(_settings, _engine);
            setup.Show();
            setup.Closed += (_, _) =>
            {
                RegisterHotkey();
                ResetWatcher();
                if (_settings.SetupDone)
                {
                    _engine.ApplySettings();
                    ShowSearchPanel();
                }
                else
                {
                    _tray?.ShowBalloonTip(8000, AppConstants.AppName,
                        "Finish the one-time setup to start searching (tray menu → Open search).",
                        WinForms.ToolTipIcon.Info);
                }
            };
        }
        else
        {
            _engine.Start(); // first pass runs in the background, pausable from the tray
        }

        if (!e.Args.Contains("--silent") && _settings.SetupDone)
        {
            Dispatcher.BeginInvoke(() => ShowSearchPanel());
        }
    }

    public void ShowSearchPanel()
    {
        if (_panel is null) return;
        _panel.ShowInTaskbar = true;
        _panel.ApplyLook();
        _panel.Show();
        _panel.Activate();
        _panel.FocusQuery();
        ErrorLog.Write($"Search panel shown, visible={_panel.IsVisible}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ErrorLog.Write($"App.OnExit called with code {e.ApplicationExitCode}");
        _showWaitRegistered?.Unregister(null);
        _showEvent?.Dispose();
        _updates?.Dispose();
        _hotkey?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _watcher?.Dispose();
        _engine?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Loud note (tray balloon): errors the user must see.</summary>
    public void ShowNote(string text)
    {
        _tray?.ShowBalloonTip(8000, AppConstants.AppName, text, WinForms.ToolTipIcon.Warning);
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    public void TogglePanel()
    {
        if (_panel is null || _settings is null) return;
        if (!_settings.SetupDone)
        {
            // Setup isn't finished: opening search opens setup instead.
            var setup = new Onboarding(_settings, _engine!);
            setup.Show();
            setup.Activate();
            setup.Closed += (_, _) =>
            {
                RegisterHotkey();
                ResetWatcher();
                if (_settings.SetupDone)
                    _engine?.ApplySettings();
            };
            return;
        }
        if (_panel.IsVisible && _panel.IsActive)
        {
            _panel.Hide();
        }
        else
        {
            _panel.ApplyLook();
            _panel.Show();
            _panel.WindowState = WindowState.Normal;
            try
            {
                var helper = new WindowInteropHelper(_panel);
                IntPtr hWnd = helper.Handle;
                if (hWnd != IntPtr.Zero)
                {
                    SetForegroundWindow(hWnd);
                    BringWindowToTop(hWnd);
                    SwitchToThisWindow(hWnd, true);
                }
            }
            catch { }
            _panel.Activate();
            _panel.FocusQuery();
        }
    }

    private void MakeTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Text = $"{AppConstants.AppName}: {Loc.AppSubtitle}",
            Icon = AppIcon.Tray,
            Visible = true,
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Открыть поиск", null, (_, _) => TogglePanel());

        // Checkable indexing item with dynamic count
        var indexItem = new WinForms.ToolStripMenuItem("Индексация") { CheckOnClick = true, Checked = true };
        indexItem.CheckedChanged += (_, _) =>
        {
            // Checked = indexing running, Unchecked = paused
            _engine?.SetPaused(!indexItem.Checked);
        };
        menu.Items.Add(indexItem);

        menu.Items.Add(Loc.Settings, null, (_, _) => OpenSettings());
        menu.Items.Add(Loc.OpenLogsBtn, null, (_, _) => ErrorLog.OpenLogsFolder());

        // About menu section
        var aboutMenu = new WinForms.ToolStripMenuItem(Loc.About);
        aboutMenu.DropDownItems.Add($"Версия: v{AppConstants.Version}", null, (_, _) => SettingsWindow.ShowAboutDialog());
        aboutMenu.DropDownItems.Add("GitHub: github.com/ex-ru/win_search", null, (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/ex-ru/win_search") { UseShellExecute = true });
            }
            catch { }
        });
        aboutMenu.DropDownItems.Add("Подробнее…", null, (_, _) => SettingsWindow.ShowAboutDialog());
        menu.Items.Add(aboutMenu);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Loc.Quit, null, (_, _) => Shutdown());

        // Dynamically update indexing counts when context menu opens
        menu.Opening += (_, _) =>
        {
            if (_engine is not null)
            {
                var (indexed, remaining) = _engine.GetIndexProgress();
                if (_engine.IsPaused)
                {
                    indexItem.Text = Loc.IndexPaused(indexed);
                }
                else if (remaining <= 0)
                {
                    indexItem.Text = Loc.IndexDone(indexed);
                }
                else
                {
                    indexItem.Text = Loc.IndexRunning(indexed, remaining);
                }
                indexItem.Checked = !_engine.IsPaused;
            }
        };

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => TogglePanel();
        _tray.BalloonTipTitle = AppConstants.AppName;
        if (_settings?.SetupDone == true)
        {
            _tray.ShowBalloonTip(4000, Loc.TrayReadyTitle, Loc.TrayReadyText(_settings.Hotkey), WinForms.ToolTipIcon.Info);
        }
    }

    private void OpenSettings()
    {
        if (_settings is null || _engine is null) return;
        if (_settingsWindow is not null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        var win = new SettingsWindow(_settings, _engine, _panel, () =>
        {
            RegisterHotkey();
            ResetWatcher();
            _engine.ApplySettings();
            _updates?.Restart();
        });
        _settingsWindow = win;
        win.Closed += (_, _) =>
        {
            _settingsWindow = null;
            RegisterHotkey();
            ResetWatcher();
            _engine.ApplySettings();
            _updates?.Restart();
        };
        win.Show();
        win.Activate();
    }

    private void ResetWatcher()
    {
        if (_settings is null || _engine is null) return;
        _watcher?.Dispose();
        _watcher = new FileSystemWatcherRunner(_settings, () => _engine.RequestIndex());
    }

    private void RegisterHotkey()
    {
        if (_settings is null) return;
        // Thread-wide hotkey (no window needed): WM_HOTKEY lands in this
        // thread's queue and is picked up below.
        _hotkey?.Dispose();
        _hotkey = new Hotkey();
        if (!_hotkey.Register(_settings.Hotkey))
            _tray?.ShowBalloonTip(5000, AppConstants.AppName,
                $"Hotkey {_settings.Hotkey} is taken by another app. Change it in Settings.", WinForms.ToolTipIcon.Warning);
        else if (_settings.Hotkey.Contains("shift") && _settings.Hotkey.Contains("alt"))
            _tray?.ShowBalloonTip(5000, AppConstants.AppName,
                "That hotkey uses Shift+Alt, which Windows also uses to switch input language. Ctrl+Alt+Space is safe.",
                WinForms.ToolTipIcon.Warning);
        if (!_hotkeyHooked)
        {
            _hotkeyHooked = true;
            ComponentDispatcher.ThreadPreprocessMessage += (ref MSG msg, ref bool handled) =>
            {
                const int WM_HOTKEY = 0x0312;
                if (msg.message == WM_HOTKEY)
                {
                    TogglePanel();
                    handled = true;
                }
            };
        }
    }
}
