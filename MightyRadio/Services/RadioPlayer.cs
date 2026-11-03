using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace MightyRadio.Services;

public enum RadioState { Stopped, Connecting, Playing, Error }

/// <summary>
/// Thin wrapper around LibVLC for audio-only streams.
///
/// Threading rules (important with LibVLC):
///  * LibVLC raises its events on its own internal threads. Calling back into LibVLC from inside an event
///    handler can deadlock, so every handler here only posts work to the WPF dispatcher (BeginInvoke).
///  * Stop()/Play() on a network stream can block for a while, so they run on a background worker,
///    serialised through a task chain so the order of requests is preserved.
///  * Public members and events of this class are used / raised on the UI thread only.
///  * Every posted event carries the Media that was playing when VLC raised it. The UI-side handler
///    ignores it unless that Media is still the current one, so a late error/end from a stream that
///    is being torn down can never stop the next station.
/// </summary>
public sealed class RadioPlayer : IDisposable
{
    private static readonly TimeSpan[] ReconnectDelays = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };

    private readonly Dispatcher _ui;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mp;
    private readonly DispatcherTimer _connectTimeout;
    private readonly DispatcherTimer _reconnectTimer;
    private Media? _media;            // UI thread: the media we want playing
    private Media? _playingMedia;     // worker thread: the media VLC is actually playing (read by event threads)
    private Task _chain = Task.CompletedTask;
    private int _generation;
    private bool _triedSubItems;
    private bool _reachedPlaying;     // this URL played at least once (so drops are worth reconnecting)
    private int _reconnectAttempt;
    private int _volume = 70;
    private double _fade = 1.0;
    private bool _disposed;

    public RadioState State { get; private set; } = RadioState.Stopped;
    /// <summary>The URL the user asked for.</summary>
    public string? CurrentUrl { get; private set; }
    /// <summary>The URL actually playing (differs from CurrentUrl for .pls/.m3u playlists).</summary>
    public string? CurrentMrl { get; private set; }
    public string? NowPlaying { get; private set; }

    /// <summary>Raised on the UI thread. Message is a friendly text for errors and reconnects.</summary>
    public event Action<RadioState, string?>? StateChanged;
    /// <summary>Raised on the UI thread with the stream's "now playing" text (ICY StreamTitle), or null.</summary>
    public event Action<string?>? NowPlayingChanged;

    public RadioPlayer(Dispatcher ui, LibVLC libVlc)
    {
        _ui = ui;
        _libVlc = libVlc;
        _mp = new MediaPlayer(_libVlc);

        _mp.Playing += (_, _) => Post(m => OnPlaying(m));
        _mp.EncounteredError += (_, _) => Post(m => OnDropped(m, "Sorry, this station isn't working right now. Please try another one."));
        _mp.EndReached += (_, _) => Post(m => OnEndReached(m));

        _connectTimeout = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromSeconds(25) };
        _connectTimeout.Tick += (_, _) =>
        {
            _connectTimeout.Stop();
            if (State != RadioState.Connecting) return;
            ++_generation; // a late "Playing" from the abandoned attempt must not revive it
            if (TryScheduleReconnect()) return;
            StopInternal();
            SetState(RadioState.Error, "This station didn't answer. It may be off air - please try another one.");
        };

        _reconnectTimer = new DispatcherTimer(DispatcherPriority.Normal, _ui);
        _reconnectTimer.Tick += (_, _) =>
        {
            _reconnectTimer.Stop();
            if (State != RadioState.Connecting || CurrentMrl == null) return;
            StartMedia(CurrentMrl, ++_generation);
        };
    }

    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            ApplyVolume();
        }
    }

    /// <summary>0..1 multiplier used by the sleep timer to fade out without moving the volume slider.</summary>
    public double FadeFactor
    {
        get => _fade;
        set
        {
            _fade = Math.Clamp(value, 0, 1);
            ApplyVolume();
        }
    }

    private void ApplyVolume()
    {
        if (_disposed) return;
        try { _mp.Volume = (int)Math.Round(_volume * _fade); } catch (Exception ex) { Storage.Log(ex, "Set volume"); }
    }

    /// <summary>Start playing a stream URL. Safe to call repeatedly; the previous stream is stopped.</summary>
    public void Play(string url)
    {
        if (_disposed) return;
        if (!StreamUrl.TryGetPlayable(url, out var uri))
        {
            SetState(RadioState.Error, "This station doesn't have a valid http/https web address.");
            return;
        }

        _reconnectTimer.Stop();
        _triedSubItems = false;
        _reachedPlaying = false;
        _reconnectAttempt = 0;
        CurrentUrl = uri.AbsoluteUri;
        CurrentMrl = CurrentUrl;
        SetNowPlaying(null);
        SetState(RadioState.Connecting, null);
        StartMedia(CurrentUrl, ++_generation);
    }

    private void StartMedia(string mrl, int gen)
    {
        _connectTimeout.Stop();
        _connectTimeout.Start();
        Media media;
        try
        {
            media = new Media(_libVlc, mrl, FromType.FromLocation);
        }
        catch (Exception ex)
        {
            Storage.Log(ex, "Creating media " + StreamUrl.ForLog(mrl));
            _connectTimeout.Stop();
            StopInternal();
            SetState(RadioState.Error, "Sorry, this station couldn't be opened.");
            return;
        }
        SwapAndPlay(media, gen);
    }

    private void SwapAndPlay(Media media, int gen)
    {
        var old = _media;
        if (old != null) old.MetaChanged -= OnMetaChanged;
        media.MetaChanged += OnMetaChanged;
        _media = media;

        Enqueue(() =>
        {
            Volatile.Write(ref _playingMedia, null); // events raised while stopping belong to nobody
            _mp.Stop();
            old?.Dispose();
            // A newer request may already be queued; only the latest one actually plays.
            if (gen == Volatile.Read(ref _generation) && !_disposed)
            {
                Volatile.Write(ref _playingMedia, media);
                if (!_mp.Play(media))
                    Post(m => OnDropped(m, "Sorry, this station couldn't be started."), media);
            }
        });
    }

    public void Stop()
    {
        if (_disposed) return;
        ++_generation;
        _connectTimeout.Stop();
        _reconnectTimer.Stop();
        StopInternal();
        CurrentUrl = null;
        CurrentMrl = null;
        SetNowPlaying(null);
        SetState(RadioState.Stopped, null);
    }

    private void StopInternal()
    {
        var old = _media;
        _media = null;
        if (old != null) old.MetaChanged -= OnMetaChanged;
        Enqueue(() =>
        {
            Volatile.Write(ref _playingMedia, null);
            _mp.Stop();
            old?.Dispose();
        });
    }

    private void Enqueue(Action work)
    {
        _chain = _chain.ContinueWith(_ =>
        {
            try { work(); }
            catch (Exception ex) { Storage.Log(ex, "Player background work"); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Called from LibVLC threads: capture which media raised it, then hop to the UI thread.</summary>
    private void Post(Action<Media> action, Media? source = null)
    {
        if (_disposed) return;
        var m = source ?? Volatile.Read(ref _playingMedia);
        if (m == null) return;
        _ui.BeginInvoke(() =>
        {
            if (_disposed || !ReferenceEquals(m, _media)) return; // stale event from an old stream
            try { action(m); }
            catch (Exception ex) { Storage.Log(ex, "Player event"); }
        });
    }

    private void OnPlaying(Media m)
    {
        if (State is RadioState.Stopped or RadioState.Error) return;
        _connectTimeout.Stop();
        _reachedPlaying = true;
        _reconnectAttempt = 0;
        ApplyVolume(); // VLC may reset volume when the audio output starts
        SetState(RadioState.Playing, null);
        RefreshNowPlaying(m);
    }

    /// <summary>Error or end of stream. Reconnect if the station had been playing, otherwise report.</summary>
    private void OnDropped(Media m, string message)
    {
        if (State == RadioState.Stopped) return;
        _connectTimeout.Stop();
        ++_generation;
        if (TryScheduleReconnect()) return;
        StopInternal();
        SetNowPlaying(null);
        SetState(RadioState.Error, message);
    }

    private bool TryScheduleReconnect()
    {
        if (!_reachedPlaying || CurrentMrl == null || _reconnectAttempt >= ReconnectDelays.Length) return false;
        var delay = ReconnectDelays[_reconnectAttempt++];
        StopInternal();
        SetState(RadioState.Connecting, $"Connection dropped - reconnecting ({_reconnectAttempt}/{ReconnectDelays.Length})…");
        _reconnectTimer.Interval = delay;
        _reconnectTimer.Start();
        return true;
    }

    private void OnEndReached(Media m)
    {
        if (State == RadioState.Stopped) return;

        // Playlist URLs (.pls/.m3u) "end" after VLC has read them; play the first real stream inside.
        if (!_triedSubItems && !_reachedPlaying)
        {
            _triedSubItems = true;
            try
            {
                using var subs = m.SubItems;
                for (int i = 0; i < subs.Count; i++)
                {
                    using var item = subs[i];
                    if (item == null || !StreamUrl.IsPlayable(item.Mrl)) continue;
                    CurrentMrl = item.Mrl;
                    StartMedia(item.Mrl, ++_generation);
                    return;
                }
            }
            catch (Exception ex) { Storage.Log(ex, "Reading playlist sub-items"); }
        }
        OnDropped(m, "The station stopped sending audio. It may be off air - try again later.");
    }

    private void OnMetaChanged(object? sender, MediaMetaChangedEventArgs e)
    {
        if (e.MetadataType != MetadataType.NowPlaying && e.MetadataType != MetadataType.Title) return;
        var media = sender as Media;
        _ui.BeginInvoke(() =>
        {
            // Only read meta for the current media (old media objects may already be disposed).
            if (_disposed || media == null || !ReferenceEquals(media, _media)) return;
            RefreshNowPlaying(media);
        });
    }

    private void RefreshNowPlaying(Media m)
    {
        try
        {
            var text = m.Meta(MetadataType.NowPlaying);
            SetNowPlaying(string.IsNullOrWhiteSpace(text) ? null : text.Trim());
        }
        catch (Exception ex) { Storage.Log(ex, "Reading now playing"); }
    }

    private void SetNowPlaying(string? text)
    {
        if (text == NowPlaying) return;
        NowPlaying = text;
        NowPlayingChanged?.Invoke(text);
    }

    private void SetState(RadioState state, string? message)
    {
        State = state;
        StateChanged?.Invoke(state, message);
    }

    /// <summary>Stop and release the player off the UI thread. True if VLC finished in time.</summary>
    public async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        if (_disposed) return true;
        _disposed = true;
        _connectTimeout.Stop();
        _reconnectTimer.Stop();
        var media = _media;
        _media = null;
        if (media != null) media.MetaChanged -= OnMetaChanged;
        var done = _chain.ContinueWith(_ =>
        {
            try { _mp.Stop(); media?.Dispose(); _mp.Dispose(); } catch (Exception ex) { Storage.Log(ex, "Player shutdown"); }
        }, TaskScheduler.Default);
        var winner = await Task.WhenAny(done, Task.Delay(timeout)).ConfigureAwait(true);
        return winner == done;
    }

    public void Dispose() => _ = ShutdownAsync(TimeSpan.FromSeconds(3));
}
