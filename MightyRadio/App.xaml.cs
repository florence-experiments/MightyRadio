using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MightyRadio.Services;

namespace MightyRadio;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Storage.Log(a.ExceptionObject as Exception, "Unhandled (domain)");
        TaskScheduler.UnobservedTaskException += (_, a) => { Storage.Log(a.Exception, "Unobserved task"); a.SetObserved(); };

        // Only one Mighty Radio at a time (avoids two radios playing over each other).
        _singleInstance = new Mutex(true, @"Local\MightyRadio.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Mighty Radio is already open - look for it on the taskbar.", "Mighty Radio",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        RadioPlayer player;
        try
        {
            // Loads the bundled VLC engine from .\libvlc\win-x64 next to MightyRadio.exe.
            Core.Initialize();
            player = new RadioPlayer(Dispatcher);
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

        var window = new MainWindow(player);
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Storage.Log(e.Exception, "Unhandled (UI)");
        e.Handled = true; // keep running - a radio should never just vanish
        try
        {
            MessageBox.Show("Oops - something went wrong, but Mighty Radio will keep going.\n\n" + e.Exception.Message,
                "Mighty Radio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
