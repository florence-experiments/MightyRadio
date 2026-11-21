using System.Windows.Threading;
using LibVLCSharp.Shared;
using Mighty.Directory;

namespace MightyTV.Services;

public enum PlaybackState { Stopped, Connecting, Playing, Error }

/// <summary>
/// Video playback around LibVLC. Same threading rules as MightyRadio.Services.RadioPlayer:
/// LibVLC callbacks only hop to the UI thread, and Stop/Play run on a background chain so they
/// cannot deadlock the dispatcher. A later merge can fold this into that player.
/// </summary>
public sealed class TvPlayer : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly LibVLC _libVlc;
    private readonly DispatcherTimer _connectTimeout;
    private Media? _media;
    private Media? _playingMedia;
    private Task _chain = Task.CompletedTask;
    private int _generation;
    private int _volume = 70;
    private string? _userAgent;
    private string? _referrer;
    private bool _triedSubItems;
    private bool _reachedPlaying;
    private bool _disposed;

    public TvPlayer(Dispatcher ui, LibVLC libVlc)
    {
        _ui = ui;
        _libVlc = libVlc;
        MediaPlayer = new MediaPlayer(_libVlc);
        MediaPlayer.Playing += (_, _) => Post(OnPlaying);
        MediaPlayer.EncounteredError += (_, _) => Post(_ => Fail("This channel isn't working right now. Please try another one."));
        MediaPlayer.EndReached += (_, _) => Post(OnEndReached);

        _connectTimeout = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromSeconds(20) };
        _connectTimeout.Tick += (_, _) =>
        {
            _connectTimeout.Stop();
            if (State != PlaybackState.Connecting) return;
            ++_generation;
            StopInternal();
            SetState(PlaybackState.Error, "This channel didn't answer. It may be off air — please try another one.");
        };
    }

    public MediaPlayer MediaPlayer { get; }
    public PlaybackState State { get; private set; } = PlaybackState.Stopped;
    public event Action<PlaybackState, string?>? StateChanged;

    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            ApplyVolume();
        }
    }

    public void Play(string url, string? userAgent, string? referrer)
    {
        if (_disposed) return;
        if (!StreamAddress.TryGetPlayable(url, out var uri))
        {
            SetState(PlaybackState.Error, "This channel doesn't have a valid http/https address.");
            return;
        }

        _triedSubItems = false;
        _reachedPlaying = false;
        _userAgent = userAgent;
        _referrer = referrer;
        SetState(PlaybackState.Connecting, null);
        StartMedia(uri.AbsoluteUri, ++_generation);
    }

    public void Stop()
    {
        if (_disposed) return;
        ++_generation;
        _connectTimeout.Stop();
        StopInternal();
        SetState(PlaybackState.Stopped, null);
    }

    /// <summary>Stop and release the player off the UI thread. True when VLC finished in time.</summary>
    public Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        if (_disposed) return Task.FromResult(true);
        _disposed = true;
        try { _connectTimeout.Stop(); } catch { }
        var media = _media;
        _media = null;
        var done = _chain.ContinueWith(_ =>
        {
            try
            {
                MediaPlayer.Stop();
                media?.Dispose();
                MediaPlayer.Dispose();
            }
            catch { }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return Task.WhenAny(done, Task.Delay(timeout)).ContinueWith(
            t => t.Result == done,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    public void Dispose() => _ = ShutdownAsync(TimeSpan.FromSeconds(2));

    private void StartMedia(string mrl, int gen)
    {
        _connectTimeout.Stop();
        _connectTimeout.Start();
        Media media;
        try
        {
            media = new Media(_libVlc, mrl, FromType.FromLocation);
            if (!string.IsNullOrEmpty(_userAgent)) media.AddOption(":http-user-agent=" + _userAgent);
            if (!string.IsNullOrEmpty(_referrer)) media.AddOption(":http-referrer=" + _referrer);
        }
        catch
        {
            _connectTimeout.Stop();
            StopInternal();
            SetState(PlaybackState.Error, "Sorry, this channel couldn't be opened.");
            return;
        }

        var old = _media;
        _media = media;
        Enqueue(() =>
        {
            Volatile.Write(ref _playingMedia, null);
            MediaPlayer.Stop();
            old?.Dispose();
            if (gen == Volatile.Read(ref _generation) && !_disposed)
            {
                Volatile.Write(ref _playingMedia, media);
                if (!MediaPlayer.Play(media))
                    Post(m => Fail("Sorry, this channel couldn't be started."), media);
            }
        });
    }

    private void StopInternal()
    {
        var old = _media;
        _media = null;
        Enqueue(() =>
        {
            Volatile.Write(ref _playingMedia, null);
            MediaPlayer.Stop();
            old?.Dispose();
        });
    }

    private void Enqueue(Action work)
    {
        _chain = _chain.ContinueWith(_ =>
        {
            try { work(); }
            catch { }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void Post(Action<Media> action, Media? source = null)
    {
        if (_disposed) return;
        var media = source ?? Volatile.Read(ref _playingMedia);
        if (media == null) return;
        _ui.BeginInvoke(() =>
        {
            if (_disposed || !ReferenceEquals(media, _media)) return;
            try { action(media); }
            catch (Exception ex) { SetState(PlaybackState.Error, "Playback failed. " + ex.Message); }
        });
    }

    private void OnPlaying(Media media)
    {
        if (State is PlaybackState.Stopped or PlaybackState.Error) return;
        _connectTimeout.Stop();
        _reachedPlaying = true;
        ApplyVolume();
        SetState(PlaybackState.Playing, null);
    }

    private void OnEndReached(Media media)
    {
        if (State == PlaybackState.Stopped) return;
        if (!_triedSubItems && !_reachedPlaying)
        {
            _triedSubItems = true;
            try
            {
                using var subs = media.SubItems;
                for (var i = 0; i < subs.Count; i++)
                {
                    using var item = subs[i];
                    if (item == null || !StreamAddress.IsPlayable(item.Mrl)) continue;
                    StartMedia(item.Mrl, ++_generation);
                    return;
                }
            }
            catch { }
        }
        Fail("The channel stopped sending video. It may be off air.");
    }

    private void Fail(string message)
    {
        if (State == PlaybackState.Stopped) return;
        _connectTimeout.Stop();
        ++_generation;
        StopInternal();
        SetState(PlaybackState.Error, message);
    }

    private void ApplyVolume()
    {
        if (_disposed) return;
        try { MediaPlayer.Volume = _volume; } catch { }
    }

    private void SetState(PlaybackState state, string? message)
    {
        State = state;
        StateChanged?.Invoke(state, message);
    }
}
