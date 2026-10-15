using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WAI.App;
using Xunit;
using WpfButton = System.Windows.Controls.Button;

namespace WAI.Tests;

[Collection("WPF search window")]
public sealed class OnboardingTests
{
    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Retry_repeats_download_and_only_final_confirmation_completes_setup(string language)
    {
        RunSta(() =>
        {
            int attempts = 0;
            int saves = 0;
            var settings = new Settings { Language = language, Hotkey = "ctrl+shift+space", Folders = [Path.Combine(Path.GetTempPath(), "wai_empty_setup_test_" + Guid.NewGuid().ToString("N"))] };
            var window = new Onboarding(settings, (_, _) =>
            {
                attempts++;
                return attempts == 1 ? Task.FromException(new IOException("simulated offline failure")) : Task.CompletedTask;
            }, () => saves++);
            try
            {
                Click(window, "Next");
                PumpUntil(() => Get<WpfButton>(window, "Next").IsEnabled);
                Assert.Equal(1, attempts);
                Assert.Equal(Loc.Retry, Get<WpfButton>(window, "Next").Content);
                Assert.DoesNotContain("simulated offline failure", Get<TextBlock>(window, "DlStatus").Text);
                Assert.False(settings.SetupDone);

                Click(window, "Next");
                PumpUntil(() => Get<WpfButton>(window, "Next").IsEnabled);
                Assert.Equal(2, attempts);
                Click(window, "Next");
                Assert.Equal(Visibility.Visible, Get<FrameworkElement>(window, "P4").Visibility);
                Assert.Contains("Ctrl+Shift+Space", Get<TextBlock>(window, "ReadyShortcut").Text);
                Assert.False(settings.SetupDone);
                Click(window, "Next");
                Assert.True(settings.SetupDone);
                Assert.True(window.Completed);
                Assert.Equal(1, saves);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Closing_during_download_cancels_work_without_completing_setup()
    {
        RunSta(() =>
        {
            CancellationToken observed = default;
            Task? work = null;
            int saves = 0;
            var settings = new Settings { Folders = [Path.Combine(Path.GetTempPath(), "wai_empty_setup_test_" + Guid.NewGuid().ToString("N"))] };
            var window = new Onboarding(settings, (_, token) =>
            {
                observed = token;
                return work = Task.Delay(Timeout.Infinite, token);
            }, () => saves++);
            Click(window, "Next");
            Assert.False(Get<WpfButton>(window, "Next").IsEnabled);
            PumpUntil(() => observed.CanBeCanceled);
            window.Close();
            Assert.True(observed.IsCancellationRequested);
            PumpUntil(() => work!.IsCompleted);
            Assert.False(window.Completed);
            Assert.False(settings.SetupDone);
            Assert.Equal(1, saves);
        });
    }

    private static T Get<T>(Window window, string name) where T : class =>
        Assert.IsAssignableFrom<T>(window.FindName(name));

    private static void Click(Window window, string name)
    {
        var button = Get<WpfButton>(window, name);
        button.RaiseEvent(new RoutedEventArgs(WpfButton.ClickEvent, button));
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        Assert.True(condition(), "The UI operation did not complete.");
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The setup test did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
