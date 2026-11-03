using System.Windows;
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

    public LibVLC? Vlc { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        try { ThemeManager.Apply(settings.Theme); }
        catch (Exception ex) { Storage.Log(ex, "Applying theme"); }

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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Storage.Log(e.Exception, "Unhandled (UI)");
        e.Handled = true; // keep running - a radio should never just vanish
        if (MainWindow == null || !MainWindow.IsLoaded)
        {
            // Failure before the window exists: don't linger invisibly.
            Shutdown(1);
            return;
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
        base.OnExit(e);
    }
}
