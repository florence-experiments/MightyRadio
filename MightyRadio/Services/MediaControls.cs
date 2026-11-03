using System.Windows.Threading;
using Windows.Media;

namespace MightyRadio.Services;

public enum MediaKey { PlayPause, Play, Pause, Stop, Next, Previous }

/// <summary>
/// Windows System Media Transport Controls: hardware media keys, Bluetooth headset buttons and the Windows
/// media overlay (volume flyout / quick settings) showing the station and song title.
/// Uses a Windows.Media.Playback.MediaPlayer purely as the SMTC host (it plays nothing; its automatic
/// command handling is switched off). If anything here fails (older Windows, policy), the app carries on
/// without it.
/// </summary>
public sealed class MediaControls : IDisposable
{
    private readonly Dispatcher _ui;
    private Windows.Media.Playback.MediaPlayer? _host;
    private SystemMediaTransportControls? _smtc;
    private DateTime _lastPress = DateTime.MinValue;
    private MediaKey _lastKey;

    public bool Available => _smtc != null;

    /// <summary>Raised on the UI thread for SMTC buttons and WM_APPCOMMAND media keys (de-duplicated).</summary>
    public event Action<MediaKey>? Pressed;

    public MediaControls(Dispatcher ui)
    {
        _ui = ui;
        try
        {
            _host = new Windows.Media.Playback.MediaPlayer();
            _host.CommandManager.IsEnabled = false;
            _smtc = _host.SystemMediaTransportControls;
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsStopEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
            _smtc.ButtonPressed += OnButtonPressed;
        }
        catch (Exception ex)
        {
            Storage.Log(ex, "Media controls unavailable");
            _smtc = null;
            try { _host?.Dispose(); } catch { }
            _host = null;
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        // Raised on a background thread.
        MediaKey? key = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => MediaKey.Play,
            SystemMediaTransportControlsButton.Pause => MediaKey.Pause,
            SystemMediaTransportControlsButton.Stop => MediaKey.Stop,
            SystemMediaTransportControlsButton.Next => MediaKey.Next,
            SystemMediaTransportControlsButton.Previous => MediaKey.Previous,
            _ => null,
        };
        if (key != null) _ui.BeginInvoke(() => Raise(key.Value));
    }

    /// <summary>Feed a key from the window's WM_APPCOMMAND hook. UI thread.</summary>
    public void Raise(MediaKey key)
    {
        // The same key press can arrive both through SMTC and WM_APPCOMMAND.
        var now = DateTime.UtcNow;
        bool samePress = (now - _lastPress).TotalMilliseconds < 300 && Similar(key, _lastKey);
        _lastPress = now;
        _lastKey = key;
        if (samePress) return;
        Pressed?.Invoke(key);
    }

    private static bool Similar(MediaKey a, MediaKey b)
    {
        static bool Toggle(MediaKey k) => k is MediaKey.Play or MediaKey.Pause or MediaKey.PlayPause;
        return a == b || (Toggle(a) && Toggle(b));
    }

    /// <summary>Update the overlay. UI thread.</summary>
    public void Update(RadioState state, string? station, string? title)
    {
        if (_smtc == null) return;
        try
        {
            _smtc.PlaybackStatus = state switch
            {
                RadioState.Playing => MediaPlaybackStatus.Playing,
                RadioState.Connecting => MediaPlaybackStatus.Changing,
                RadioState.Stopped when station == null => MediaPlaybackStatus.Closed,
                _ => MediaPlaybackStatus.Stopped,
            };
            var d = _smtc.DisplayUpdater;
            d.Type = MediaPlaybackType.Music;
            d.MusicProperties.Title = !string.IsNullOrWhiteSpace(title) ? title : (station ?? "Mighty Radio");
            d.MusicProperties.Artist = station ?? "";
            d.MusicProperties.AlbumTitle = "Mighty Radio";
            d.Update();
        }
        catch (Exception ex) { Storage.Log(ex, "Updating media controls"); }
    }

    public void Dispose()
    {
        try
        {
            if (_smtc != null)
            {
                _smtc.ButtonPressed -= OnButtonPressed;
                _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                _smtc.DisplayUpdater.ClearAll();
                _smtc.DisplayUpdater.Update();
            }
            _host?.Dispose();
        }
        catch { }
        _smtc = null;
        _host = null;
    }
}
