using System.Diagnostics;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MightyRadio.Services;

namespace MightyRadio;

public partial class App : Application
{
    private const string MutexName = @"Local\MightyRadio.SingleInstance";
    private const string ActivateEventName = @"Local\MightyRadio.Activate";

    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;
    private DateTime _lastErrorBox = DateTime.MinValue;
    private const string SafeModeArg = "--safe-mode";
    private bool _safeMode;           // restarted after a failed start: Dark theme, no second restart
    private bool _startupComplete;    // the main window has drawn once
    private bool _startupFailed;
    private bool _restartInSafeMode;
    private bool _themeFallbackDone;

    public LibVLC? Vlc { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _safeMode = e.Args.Any(a => string.Equals(a, SafeModeArg, StringComparison.OrdinalIgnoreCase));

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Storage.Log(a.ExceptionObject as Exception, "Unhandled (domain)");
        TaskScheduler.UnobservedTaskException += (_, a) => { Storage.Log(a.Exception, "Unobserved task"); a.SetObserved(); };

        // Only one Mighty Radio at a time. A second launch asks the first one to come to the front
        // (it may be hidden in the notification area) and then quits.
        _singleInstance = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var ev))
                {
                    using (ev) ev.Set();
                }
                else
                {
                    MessageBox.Show("Mighty Radio is already open - look for it on the taskbar or in the notification area.",
                        "Mighty Radio", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex) { Storage.Log(ex, "Signalling the running instance"); }
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(0);
            return;
        }

        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) =>
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront()), null, Timeout.Infinite, false);
        }
        catch (Exception ex) { Storage.Log(ex, "Single-instance activation event"); }

        var settings = Storage.LoadSettings();
        if (_safeMode) settings.Theme = ThemeManager.FallbackId;
        // Checks the theme off-screen first, so a broken palette or style falls back to Dark instead of
        // failing halfway through drawing the window.
        string? themeNotice = null;
        try
        {
            themeNotice = ThemeManager.ApplyAtStartup(settings.Theme);
            if (themeNotice != null || _safeMode)
            {
                settings.Theme = ThemeManager.Current;
                Storage.SaveSettings(settings);
            }
        }
        catch (Exception ex) { Storage.Log(ex, "Applying theme"); }
        if (_safeMode)
            themeNotice = "Mighty Radio restarted with the Dark theme after a problem opening its window. " +
                          "Details are in " + Storage.LogPath + ".";

        try
        {
            // Loads the bundled VLC engine from .\libvlc\win-x64 next to MightyRadio.exe.
            Core.Initialize();
            // Audio only; reconnect on flaky HTTP streams; a little extra buffering for Wi-Fi.
            Vlc = new LibVLC("--no-video", "--http-reconnect", "--network-caching=2000", "--no-stats");
            Vlc.SetUserAgent("Mighty Radio " + AppInfo.Version, AppInfo.UserAgent);
        }
        catch (Exception ex)
        {
            Storage.Log(ex, "Starting the VLC audio engine");
            MessageBox.Show(
                "Mighty Radio couldn't start its audio engine.\n\n" +
                "Please make sure you unzipped the whole folder (including the 'libvlc' folder) " +
                "and are running MightyRadio.exe from inside it.",
                "Mighty Radio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try
        {
            var window = new MainWindow(Vlc, settings);
            MainWindow = window;
            window.ContentRendered += (_, _) => _startupComplete = true;
            if (themeNotice != null) window.ShowNotice(themeNotice, isError: true);
            window.Show();
        }
        catch (Exception ex)
        {
            // Never leave a window-less process holding the single-instance lock.
            Storage.Log(ex, "Opening the main window");
            MessageBox.Show("Mighty Radio couldn't open its window.\n\n" + ex.Message +
                            "\n\nDetails were saved to " + Storage.LogPath,
                "Mighty Radio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// True when the exception came from building or laying out controls (a bad style, template or resource).
    /// WPF abandons the layout pass when that happens, so the window is left half-drawn: carrying on would show
    /// overlapping, partly-updated controls rather than a working app.
    /// </summary>
    private static bool IsLayoutFailure(Exception ex)
    {
        for (var x = ex; x != null; x = x.InnerException)
        {
            if (x is XamlParseException) return true;
            var st = x.StackTrace ?? "";
            if (st.Contains("System.Windows.UIElement.Measure") || st.Contains("System.Windows.UIElement.Arrange") ||
                st.Contains("System.Windows.FrameworkElement.ApplyTemplate") || st.Contains("System.Windows.ContextLayoutManager") ||
                st.Contains("System.Windows.FrameworkTemplate"))
                return true;
        }
        return false;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true; // keep running - a radio should never just vanish
        if (_startupFailed) return; // already closing down; ignore the echoes
        Storage.Log(e.Exception, "Unhandled (UI)");
        if (MainWindow == null)
        {
            // Failure before the window exists: don't linger invisibly.
            Shutdown(1);
            return;
        }
        if (IsLayoutFailure(e.Exception))
        {
            if (!_startupComplete) { FailStartup(e.Exception); return; }
            if (!_themeFallbackDone && ThemeManager.Current != ThemeManager.FallbackId)
            {
                // A theme that broke after start-up: drop back to Dark once, then carry on as usual.
                _themeFallbackDone = true;
                var broken = ThemeManager.Name(ThemeManager.Current);
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (ThemeManager.TryApply(ThemeManager.FallbackId) == null && MainWindow is MainWindow w)
                        w.ShowNotice($"The {broken} theme hit a problem, so Mighty Radio switched to Dark. Details are in {Storage.LogPath}.", isError: true);
                });
                return;
            }
        }
        // A repeating error (e.g. in a binding) must not produce an endless stream of message boxes.
        if (DateTime.UtcNow - _lastErrorBox < TimeSpan.FromSeconds(30)) return;
        _lastErrorBox = DateTime.UtcNow;
        try
        {
            MessageBox.Show("Oops - something went wrong, but Mighty Radio will keep going.\n\n" + e.Exception.Message,
                "Mighty Radio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
    }

    /// <summary>
    /// The window failed while being built. Hide it at once (no half-drawn second layout on screen), explain,
    /// then exit and start again once with the Dark theme. A failure in that safe-mode start just exits.
    /// </summary>
    private void FailStartup(Exception ex)
    {
        _startupFailed = true;
        Storage.Log(ex, _safeMode ? "Window failed to build (safe mode)" : "Window failed to build; restarting with the Dark theme");
        try { MainWindow?.Hide(); } catch { }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var restart = !_safeMode;
            try
            {
                MessageBox.Show(
                    (restart
                        ? "Mighty Radio hit a problem while opening its window, so it will close and restart with the Dark theme.\n\n"
                        : "Mighty Radio couldn't open its window, even with the Dark theme.\n\n") +
                    ex.Message + "\n\nDetails were saved to " + Storage.LogPath,
                    "Mighty Radio", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            _restartInSafeMode = restart;
            if (MainWindow is MainWindow w) w.RequestExit();
            else Shutdown(1);
        });
    }

    /// <summary>Called by the main window after it has stopped playback and recordings.</summary>
    public void FinishShutdown(bool vlcIdle)
    {
        // Dispose LibVLC last, and only if every player using it has finished stopping;
        // otherwise let process exit clean up rather than race a Stop() still in progress.
        if (vlcIdle)
        {
            try { Vlc?.Dispose(); } catch (Exception ex) { Storage.Log(ex, "Disposing LibVLC"); }
        }
        Vlc = null;
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _activateWait?.Unregister(null); } catch { }
        _activateEvent?.Dispose();
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        _singleInstance = null;
        if (_restartInSafeMode)
        {
            try
            {
                var s = Storage.LoadSettings();
                s.Theme = ThemeManager.FallbackId;
                Storage.SaveSettings(s);
                if (Environment.ProcessPath is { } exe)
                    Process.Start(new ProcessStartInfo(exe, SafeModeArg) { UseShellExecute = false });
            }
            catch (Exception ex) { Storage.Log(ex, "Restarting in safe mode"); }
        }
        base.OnExit(e);
    }
}
