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
/// </summary>
public sealed class RadioPlayer : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly MediaPlayer _mp;
    private readonly DispatcherTimer _connectTimeout;
    private Media? _media;
    private Task _chain = Task.CompletedTask;
    private int _generation;
    private bool _triedSubItems;
    private int _volume = 70;
    private bool _disposed;

    public LibVLC LibVlc { get; }
    public RadioState State { get; private set; } = RadioState.Stopped;
    public string? CurrentUrl { get; private set; }

    /// <summary>Raised on the UI thread. Message is a friendly text for errors.</summary>
    public event Action<RadioState, string?>? StateChanged;
    /// <summary>Raised on the UI thread with the stream's "now playing" text (ICY StreamTitle), or null.</summary>
    public event Action<string?>? NowPlayingChanged;

    public RadioPlayer(Dispatcher ui)
    {
        _ui = ui;
        // Audio only; reconnect automatically on flaky HTTP streams; a little extra buffering for Wi-Fi.
        LibVlc = new LibVLC("--no-video", "--http-reconnect", "--network-caching=2000", "--no-stats");
        _mp = new MediaPlayer(LibVlc);

        _mp.Playing += (_, _) => Post(gen => OnPlaying(gen));
        _mp.EncounteredError += (_, _) => Post(gen => OnError(gen, "Sorry, this station isn't working right now. Please try another one."));
        _mp.EndReached += (_, _) => Post(gen => OnEndReached(gen));

        _connectTimeout = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromSeconds(25) };
        _connectTimeout.Tick += (_, _) =>
        {
            _connectTimeout.Stop();
            if (State == RadioState.Connecting)
            {
                StopInternal();
                SetState(RadioState.Error, "This station didn't answer. It may be off air - please try another one.");
            }
        };
    }

    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            if (_disposed) return;
            try { _mp.Volume = _volume; } catch (Exception ex) { Storage.Log(ex, "Set volume"); }
        }
    }

    /// <summary>Start playing a stream URL. Safe to call repeatedly; the previous stream is stopped.</summary>
    public void Play(string url)
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out _))
        {
            SetState(RadioState.Error, "This station doesn't have a valid web address.");
            return;
        }

        int gen = ++_generation;
        _triedSubItems = false;
        CurrentUrl = url.Trim();
        NowPlayingChanged?.Invoke(null);
        SetState(RadioState.Connecting, null);
        _connectTimeout.Stop();
        _connectTimeout.Start();

        Media media;
        try
        {
            media = new Media(LibVlc, CurrentUrl, FromType.FromLocation);
        }
        catch (Exception ex)
        {
            Storage.Log(ex, "Creating media " + url);
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
            _mp.Stop();
            old?.Dispose();
            // A newer request may already be queued; only the latest one actually plays.
            if (gen == Volatile.Read(ref _generation) && !_disposed)
            {
                if (!_mp.Play(media))
                    Post(g => OnError(g, "Sorry, this station couldn't be started."));
            }
        });
    }

    public void Stop()
    {
        if (_disposed) return;
        ++_generation;
        _connectTimeout.Stop();
        StopInternal();
        CurrentUrl = null;
        NowPlayingChanged?.Invoke(null);
        SetState(RadioState.Stopped, null);
    }

    private void StopInternal()
    {
        var old = _media;
        _media = null;
        if (old != null) old.MetaChanged -= OnMetaChanged;
        Enqueue(() =>
        {
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

    /// <summary>Called from LibVLC threads: capture generation and hop to the UI thread.</summary>
    private void Post(Action<int> action)
    {
        if (_disposed) return;
        int gen = Volatile.Read(ref _generation);
        _ui.BeginInvoke(() =>
        {
            if (_disposed) return;
            try { action(gen); }
            catch (Exception ex) { Storage.Log(ex, "Player event"); }
        });
    }

    private void OnPlaying(int gen)
    {
        if (gen != _generation) return;
        _connectTimeout.Stop();
        Volume = _volume; // VLC may reset volume when the audio output starts
        SetState(RadioState.Playing, null);
        RefreshNowPlaying();
    }

    private void OnError(int gen, string message)
    {
        if (gen != _generation || State == RadioState.Stopped) return;
        _connectTimeout.Stop();
        StopInternal();
        NowPlayingChanged?.Invoke(null);
        SetState(RadioState.Error, message);
    }

    private void OnEndReached(int gen)
    {
        if (gen != _generation || _media == null || State == RadioState.Stopped) return;

        // Playlist URLs (.pls/.m3u) "end" after VLC has read them; play the first real stream inside.
        if (!_triedSubItems)
        {
            _triedSubItems = true;
            try
            {
                using var subs = _media.SubItems;
                if (subs.Count > 0)
                {
                    var first = subs[0];
                    if (first != null)
                    {
                        var sub = new Media(LibVlc, first.Mrl, FromType.FromLocation);
                        first.Dispose();
                        SwapAndPlay(sub, gen);
                        return;
                    }
                }
            }
            catch (Exception ex) { Storage.Log(ex, "Reading playlist sub-items"); }
        }
        OnError(gen, "The station stopped sending audio. It may be off air - try again later.");
    }

    private void OnMetaChanged(object? sender, MediaMetaChangedEventArgs e)
    {
        if (e.MetadataType != MetadataType.NowPlaying && e.MetadataType != MetadataType.Title) return;
        var media = sender as Media;
        _ui.BeginInvoke(() =>
        {
            // Only read meta for the current media (old media objects may already be disposed).
            if (_disposed || media == null || !ReferenceEquals(media, _media)) return;
            RefreshNowPlaying();
        });
    }

    private void RefreshNowPlaying()
    {
        try
        {
            var text = _media?.Meta(MetadataType.NowPlaying);
            NowPlayingChanged?.Invoke(string.IsNullOrWhiteSpace(text) ? null : text.Trim());
        }
        catch (Exception ex) { Storage.Log(ex, "Reading now playing"); }
    }

    private void SetState(RadioState state, string? message)
    {
        State = state;
        StateChanged?.Invoke(state, message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connectTimeout.Stop();
        var media = _media;
        _media = null;
        if (media != null) media.MetaChanged -= OnMetaChanged;
        // Give VLC a moment to stop cleanly, but never hang the app on exit.
        var done = _chain.ContinueWith(_ =>
        {
            try { _mp.Stop(); media?.Dispose(); _mp.Dispose(); } catch { }
        }, TaskScheduler.Default);
        done.Wait(TimeSpan.FromSeconds(3));
    }
}
