using System.Windows;
using LibVLCSharp.Shared;

namespace MightyTV;

public partial class App : Application
{
    public LibVLC? Vlc { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            try
            {
                MessageBox.Show(args.Exception.Message, "Mighty TV", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
        };

        try
        {
            Core.Initialize();
            Vlc = new LibVLC("--http-reconnect", "--network-caching=2000", "--live-caching=2000", "--no-stats", "--no-osd");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Mighty TV couldn't start its video engine.\n\n" +
                "Unzip the whole folder (including libvlc) and run MightyTV.exe from inside it.\n\n" + ex.Message,
                "Mighty TV", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try
        {
            var window = new MainWindow(Vlc);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Mighty TV couldn't open its window.\n\n" + ex.Message,
                "Mighty TV", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var idle = MainWindow is not MainWindow window || window.PlayerIdle;
        if (idle)
        {
            try { Vlc?.Dispose(); } catch { }
        }
        Vlc = null;
        base.OnExit(e);
    }
}
