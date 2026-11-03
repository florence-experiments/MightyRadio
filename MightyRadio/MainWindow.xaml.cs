using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MightyRadio.Models;
using MightyRadio.Services;
using MightyRadio.Views;

namespace MightyRadio;

public partial class MainWindow : Window
{
    private const string AnyCategory = "All categories";
    private static readonly string[] CategoryOrder =
    {
        "Air / ATC", "Marine / VHF", "Space", "Public safety", "Amateur / ham", "Weather", "Numbers / shortwave / utility", "Rail",
    };

    private sealed record SleepOption(string Label, int Minutes) { public override string ToString() => Label; }
    private sealed record DayOption(string Label, DateTime Date) { public override string ToString() => Label; }
    private sealed record RepeatOption(string Label, ScheduleRepeat Repeat) { public override string ToString() => Label; }

    private const int SleepOff = 0, SleepCustom = -1;
    private static readonly TimeSpan FadeLength = TimeSpan.FromSeconds(60);

    private readonly RadioPlayer _player;
    private readonly Recorder _recorder;
    private readonly RadioBrowserClient _api = new();
    private readonly AppSettings _settings;
    private readonly HistoryStore _history;
    private readonly Scheduler _scheduler;
    private readonly MediaControls _media;
    private readonly TrayIcon _tray;
    private readonly ObservableCollection<Station> _results = new();
    private readonly ObservableCollection<Station> _favourites;
    private readonly ObservableCollection<Station> _scannerView = new();
    private readonly DispatcherTimer _recordTimer;
    private readonly DispatcherTimer _sleepTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private List<Station> _scannerAll = new();
    private CancellationTokenSource? _searchCts;
    private Station? _current;
    private DateTime? _sleepEnd;
    private int _playRequest;
    private bool _suppressAutoSearch = true;
    private bool _suppressUiEvents = true;
    private bool _closing;          // shutdown has started
    private bool _shutdownDone;     // safe to let the window close
    private bool _exitRequested;    // user chose Exit from the tray: don't hide to tray
    private bool _miniMode;
    private double _fullHeight;
    private bool _hiddenToTray;

    public MainWindow(LibVLC vlc, AppSettings settings)
    {
        _settings = settings;
        _player = new RadioPlayer(Dispatcher, vlc); // assigned before InitializeComponent: XAML may raise ValueChanged early
        var favs = Storage.LoadFavourites();
        _favourites = new ObservableCollection<Station>(favs.Where(f => !string.IsNullOrWhiteSpace(f.StreamUrl)));
        InitializeComponent();

        _recorder = new Recorder(vlc, Dispatcher);
        _recorder.Failed += msg => _ = StopRecordingAsync(msg, isError: true);
        _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recordTimer.Tick += (_, _) => UpdateRecordUi();
        _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sleepTimer.Tick += (_, _) => SleepTick();

        _history = new HistoryStore(Dispatcher);
        _scheduler = new Scheduler(Dispatcher, () => new Recorder(vlc, Dispatcher));
        _scheduler.Message += OnSchedulerMessage;
        _scheduler.Changed += UpdateScheduleStatus;

        _media = new MediaControls(Dispatcher);
        _media.Pressed += OnMediaKey;

        _tray = new TrayIcon();
        _tray.ShowRequested += BringToFront;
        _tray.PlayStopRequested += TogglePlayStop;
        _tray.ExitRequested += () => { _exitRequested = true; Close(); };

        if (_settings.WindowWidth >= MinWidth && _settings.WindowWidth < 5000) Width = _settings.WindowWidth;
        if (_settings.WindowHeight >= MinHeight && _settings.WindowHeight < 4000) Height = _settings.WindowHeight;

        ResultsList.ItemsSource = _results;
        FavouritesList.ItemsSource = _favourites;
        ScannerList.ItemsSource = _scannerView;
        HistoryList.ItemsSource = _history.Items;
        ScheduleList.ItemsSource = _scheduler.Entries;

        _player.StateChanged += OnPlayerStateChanged;
        _player.NowPlayingChanged += OnNowPlayingChanged;
        _player.Volume = Math.Clamp(_settings.Volume, 0, 100);
        VolumeSlider.Value = _player.Volume;

        SortBox.ItemsSource = new List<SortOption>
        {
            new() { Label = "Most popular", Order = "votes", Reverse = true },
            new() { Label = "Most played", Order = "clickcount", Reverse = true },
            new() { Label = "Trending now", Order = "clicktrend", Reverse = true },
            new() { Label = "Name (A–Z)", Order = "name", Reverse = false },
        };
        SortBox.SelectedItem = ((List<SortOption>)SortBox.ItemsSource).FirstOrDefault(o => o.Order == _settings.SortOrder)
                               ?? ((List<SortOption>)SortBox.ItemsSource)[0];

        // Placeholder until the full country list arrives from Radio Browser.
        var initialCountries = new List<CountryOption>
        {
            new() { Code = "", Name = "Any country" },
            new() { Code = "GB", Name = CountryNames.Get("GB", "United Kingdom") },
        };
        CountryBox.ItemsSource = initialCountries;
        CountryBox.SelectedItem = initialCountries.FirstOrDefault(c => c.Code == (_settings.CountryCode ?? "GB")) ?? initialCountries[1];
        CountryBox.SelectionChanged += (_, _) => { if (!_suppressAutoSearch) _ = SearchAsync(); };
        SortBox.SelectionChanged += (_, _) => { if (!_suppressAutoSearch) _ = SearchAsync(); };

        // Sleep timer
        SleepBox.ItemsSource = new List<SleepOption>
        {
            new("Sleep: off", SleepOff), new("Sleep: 15 min", 15), new("Sleep: 30 min", 30), new("Sleep: 45 min", 45),
            new("Sleep: 1 hour", 60), new("Sleep: 90 min", 90), new("Sleep: 2 hours", 120), new("Sleep: custom…", SleepCustom),
        };
        SleepBox.SelectedIndex = 0;
        SleepCustomBox.Text = Math.Clamp(_settings.LastSleepMinutes, 1, 720).ToString();

        // Schedule form
        ScheduleRepeatBox.ItemsSource = new List<RepeatOption>
        {
            new("Once", ScheduleRepeat.Once), new("Every day", ScheduleRepeat.Daily), new("Weekdays (Mon–Fri)", ScheduleRepeat.Weekdays),
        };
        ScheduleRepeatBox.SelectedIndex = 0;
        RefreshScheduleForm();

        // Settings
        ThemeBox.ItemsSource = ThemeManager.Themes;
        QuickThemeBox.ItemsSource = ThemeManager.Themes;
        SelectThemeInBoxes(ThemeManager.Current);
        TrayBox.IsChecked = _settings.MinimiseToTray;
        CloseToTrayBox.IsChecked = _settings.CloseToTrayWhenScheduled;
        RecordingsFolderText.Text = "Recordings are saved in " + Storage.RecordingsFolder +
                                    ". The first time you record, Mighty Radio asks you to read and accept a short notice.";
        DataFolderText.Text = "Favourites, settings, history and schedules are kept in " + Storage.DataFolder + ".";
        AboutText.Text = $"Mighty Radio {AppInfo.Version}  ·  MIT licence, provided as-is without warranty.\n" +
                         $"Plays streams with LibVLC (LGPL 2.1+). Station directory: radio-browser.info.\n{AppInfo.RepoUrl}";
        if (!_media.Available)
            MediaKeysText.Text += " (The Windows media overlay isn't available on this PC; keyboard media keys still work while the window is focused.)";

        _favourites.CollectionChanged += (_, _) => { UpdateFavouritesUi(); };
        _history.Items.CollectionChanged += (_, _) => UpdateHistoryUi();
        _scheduler.Entries.CollectionChanged += (_, _) => UpdateScheduleStatus();
        UpdateFavouritesUi();
        UpdateHistoryUi();
        LoadScannerList();

        if (_settings.LastStation is { } last && StreamUrl.IsPlayable(last.StreamUrl))
        {
            _current = last;
            StationNameText.Text = last.DisplayName;
            NowPlayingText.Text = "Press Play to listen";
        }
        UpdateControls();
        UpdateMediaOverlay();
        _suppressUiEvents = false;

        Loaded += async (_, _) =>
        {
            if (Storage.FavouritesReadFailed)
                ShowMessage("Your favourites file couldn't be read, so a copy was kept next to it (favourites.json.bad-…). " +
                            "Mighty Radio won't overwrite it unless you change your favourites.", isError: true);
            _scheduler.Start();
            UpdateScheduleStatus();
            await LoadCountriesAsync();
            if (_closing) return;
            _suppressAutoSearch = false;
            await SearchAsync();
        };
        StateChanged += OnWindowStateChanged;
        Closing += OnClosing;
        if (Application.Current != null) Application.Current.SessionEnding += (_, _) => SaveState();
    }

    // ------------------------------------------------------------------ Window / tray / media keys

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try { HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc); }
        catch (Exception ex) { Storage.Log(ex, "Media key hook"); }
    }

    private const int WM_APPCOMMAND = 0x0319;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_APPCOMMAND) return IntPtr.Zero;
        int cmd = (short)(((long)lParam >> 16) & 0xFFFF) & ~0xF000;
        MediaKey? key = cmd switch
        {
            14 => MediaKey.PlayPause,   // APPCOMMAND_MEDIA_PLAY_PAUSE
            13 => MediaKey.Stop,        // APPCOMMAND_MEDIA_STOP
            11 => MediaKey.Next,        // APPCOMMAND_MEDIA_NEXTTRACK
            12 => MediaKey.Previous,    // APPCOMMAND_MEDIA_PREVIOUSTRACK
            46 => MediaKey.Play,        // APPCOMMAND_MEDIA_PLAY
            47 => MediaKey.Pause,       // APPCOMMAND_MEDIA_PAUSE
            _ => null,
        };
        if (key == null) return IntPtr.Zero;
        handled = true;
        _media.Raise(key.Value);
        return new IntPtr(1);
    }

    private void OnMediaKey(MediaKey key)
    {
        if (_closing) return;
        bool active = _player.State is RadioState.Playing or RadioState.Connecting;
        switch (key)
        {
            case MediaKey.PlayPause:
                TogglePlayStop();
                break;
            case MediaKey.Play:
                if (!active && _current != null) Play(_current);
                break;
            case MediaKey.Pause:
            case MediaKey.Stop:
                if (active || _recorder.IsRecording) StopButton_Click(this, new RoutedEventArgs());
                break;
            case MediaKey.Next:
            case MediaKey.Previous:
                StepFavourite(key == MediaKey.Next ? 1 : -1);
                break;
        }
    }

    private void TogglePlayStop()
    {
        if (_closing) return;
        bool active = _player.State is RadioState.Playing or RadioState.Connecting;
        if (active) StopButton_Click(this, new RoutedEventArgs());
        else if (_current != null) Play(_current);
        else ShowMessage("Pick a station from the list first, then press Play.", isError: true);
    }

    private void StepFavourite(int dir)
    {
        if (_favourites.Count == 0)
        {
            ShowMessage("Next and Previous step through your favourites. Add some first.", isError: false);
            return;
        }
        int i = _current == null ? -1 : _favourites.ToList().FindIndex(f => f.Key == _current.Key);
        int next = i < 0 ? (dir > 0 ? 0 : _favourites.Count - 1) : ((i + dir) % _favourites.Count + _favourites.Count) % _favourites.Count;
        var s = _favourites[next];
        FavouritesList.SelectedItem = s;
        Play(s);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.MinimiseToTray && !_closing) HideToTray(notify: false);
    }

    private void HideToTray(bool notify)
    {
        _tray.Visible = true;
        _hiddenToTray = true;
        Hide();
        UpdateTray();
        if (notify)
            _tray.Notify("Mighty Radio is still running",
                _scheduler.HasPending ? "Scheduled recordings will still happen. Right-click this icon and choose Exit to quit."
                                      : "Right-click this icon and choose Exit to quit.");
    }

    /// <summary>Show, restore and activate (tray click, second launch).</summary>
    public void BringToFront()
    {
        if (_closing) return;
        BringToFrontQuietly();
        Activate();
        Topmost = !Topmost; Topmost = _miniMode; // nudge to the front without stealing permanent topmost
    }

    private void BringToFrontQuietly()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        _hiddenToTray = false;
        _tray.Visible = false;
    }

    private void MiniButton_Click(object sender, RoutedEventArgs e)
    {
        _miniMode = !_miniMode;
        if (_miniMode)
        {
            _fullHeight = ActualHeight;
            Tabs.Visibility = Visibility.Collapsed;
            Footer.Visibility = Visibility.Collapsed;
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;
            Topmost = true;
            MiniButtonText.Text = "Full window";
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            Tabs.Visibility = Visibility.Visible;
            Footer.Visibility = Visibility.Visible;
            MinHeight = 580;
            Height = Math.Max(_fullHeight, MinHeight);
            Topmost = false;
            MiniButtonText.Text = "Mini player";
        }
        AutomationProperties.SetName(MiniButton, _miniMode ? "Full window" : "Mini player");
    }

    private void UpdateTray()
    {
        bool active = _player.State is RadioState.Playing or RadioState.Connecting;
        var tip = active && _current != null ? _current.DisplayName : (_scheduler.IsRecording ? "recording" : null);
        _tray.Update(active, tip);
    }

    private void UpdateMediaOverlay() =>
        _media.Update(_player.State, _current?.DisplayName, _player.State == RadioState.Playing ? _player.NowPlaying : null);

    // ------------------------------------------------------------------ Playback

    private async void Play(Station? station)
    {
        if (station == null || _closing) return;
        if (station.IsWebLink)
        {
            OpenInBrowser(station.StreamUrl);
            return;
        }
        if (!StreamUrl.IsPlayable(station.StreamUrl))
        {
            ShowMessage("This station's address isn't a web stream (only http and https links can be played).", isError: true);
            return;
        }

        int request = ++_playRequest;
        try
        {
            if (_recorder.IsRecording && _current?.Key != station.Key)
            {
                await StopRecordingAsync("Recording saved (you changed station).", isError: false);
                // Another station was chosen (or the window closed) while the recording was being finalised.
                if (request != _playRequest || _closing) return;
            }

            _current = station;
            if (_sleepEnd == null) _player.FadeFactor = 1;
            StationNameText.Text = station.DisplayName;
            NowPlayingText.Text = "";
            HideMessage();
            _player.Play(station.StreamUrl);
            if (!station.IsScanner) _api.ReportClick(station.StationUuid);

            _settings.LastStation = station;
            Storage.SaveSettings(_settings);
            UpdateControls();
            UpdateMediaOverlay();
            UpdateTray();
        }
        catch (Exception ex)
        {
            Storage.Log(ex, "Play");
            ShowMessage("Sorry, that station couldn't be started.", isError: true);
        }
    }

    private void OnPlayerStateChanged(RadioState state, string? message)
    {
        switch (state)
        {
            case RadioState.Connecting:
                SetStatus(message != null ? "RECONNECTING…" : "CONNECTING…");
                if (message != null) ShowMessage(message, isError: true);
                else NowPlayingText.Text = "Tuning in…";
                break;
            case RadioState.Playing:
                SetStatus("● LIVE");
                if (NowPlayingText.Text == "Tuning in…") NowPlayingText.Text = _player.NowPlaying ?? "";
                HideMessage();
                break;
            case RadioState.Stopped:
                SetStatus("STOPPED");
                NowPlayingText.Text = "";
                break;
            case RadioState.Error:
                SetStatus("NOT AVAILABLE");
                NowPlayingText.Text = "";
                ShowMessage(message ?? "Sorry, something went wrong with this station.", isError: true);
                break;
        }

        if ((state == RadioState.Error || state == RadioState.Stopped) && _recorder.IsRecording)
            _ = StopRecordingAsync(state == RadioState.Error ? "Playback failed, recording saved." : "Recording saved.", isError: state == RadioState.Error);

        UpdateControls();
        UpdateMediaOverlay();
        UpdateTray();
    }

    private void OnNowPlayingChanged(string? text)
    {
        NowPlayingText.Text = text ?? "";
        if (_current != null && !string.IsNullOrWhiteSpace(text) && IsUsefulTitle(text, _current))
            _history.Add(_current.DisplayName, text);
        UpdateMediaOverlay();
    }

    private static bool IsUsefulTitle(string title, Station s)
    {
        var t = title.Trim();
        if (t.Length < 2 || t.All(c => !char.IsLetterOrDigit(c))) return false; // "-", " - ", "..."
        return !string.Equals(t, s.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(string text)
    {
        StatusLabel.Text = text;
        Announce(StatusLabel);
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            ShowMessage("Pick a station from the list first, then press Play.", isError: true);
            return;
        }
        Play(_current);
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _playRequest++; // cancels a station change still waiting for a recording to finish
        CancelSleep();
        _player.Stop();
        if (_recorder.IsRecording) await StopRecordingAsync("Recording saved.", isError: false);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player == null) return;
        _player.Volume = (int)Math.Round(e.NewValue);
        if (VolumeText != null) VolumeText.Text = _player.Volume.ToString();
    }

    // ------------------------------------------------------------------ Sleep timer

    private void SleepBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || SleepBox.SelectedItem is not SleepOption opt) return;
        SleepCustomPanel.Visibility = opt.Minutes == SleepCustom ? Visibility.Visible : Visibility.Collapsed;
        if (opt.Minutes == SleepOff) { CancelSleep(); return; }
        if (opt.Minutes == SleepCustom) { SleepCustomBox.Focus(); SleepCustomBox.SelectAll(); return; }
        StartSleep(opt.Minutes);
    }

    private void SleepCustomSet_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SleepCustomBox.Text.Trim(), out var m) || m < 1 || m > 720)
        {
            ShowMessage("Type a number of minutes between 1 and 720 for the sleep timer.", isError: true);
            return;
        }
        _settings.LastSleepMinutes = m;
        Storage.SaveSettings(_settings);
        StartSleep(m);
    }

    private void SleepCustomBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; SleepCustomSet_Click(sender, e); }
    }

    private void StartSleep(int minutes)
    {
        _sleepEnd = DateTime.Now.AddMinutes(minutes);
        _player.FadeFactor = 1;
        _sleepTimer.Start();
        SleepTick();
        ShowMessage($"Sleep timer set: the radio will fade out and stop at {_sleepEnd:HH:mm}.", isError: false);
    }

    private void CancelSleep(bool restoreVolume = true)
    {
        bool wasOn = _sleepEnd != null;
        _sleepEnd = null;
        _sleepTimer.Stop();
        if (wasOn && restoreVolume) _player.FadeFactor = 1;
        SleepStatusText.Visibility = Visibility.Collapsed;
        SleepCustomPanel.Visibility = Visibility.Collapsed;
        if (wasOn && SleepBox.SelectedIndex != 0)
        {
            _suppressUiEvents = true;
            SleepBox.SelectedIndex = 0;
            _suppressUiEvents = false;
        }
    }

    private void SleepTick()
    {
        if (_sleepEnd == null) return;
        var left = _sleepEnd.Value - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            bool wasActive = _player.State is RadioState.Playing or RadioState.Connecting;
            _player.FadeFactor = 0;               // stay silent while stopping; Play restores the volume
            CancelSleep(restoreVolume: false);
            if (wasActive || _recorder.IsRecording) StopButton_Click(this, new RoutedEventArgs());
            ShowMessage("Sleep timer: the radio has been stopped. Good night.", isError: false);
            return;
        }
        _player.FadeFactor = left < FadeLength ? left.TotalSeconds / FadeLength.TotalSeconds : 1;
        SleepStatusText.Text = left.TotalHours >= 1 ? $"SLEEP {(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}" : $"SLEEP {left.Minutes:00}:{left.Seconds:00}";
        SleepStatusText.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ Recording

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            await StopRecordingAsync("Recording saved.", isError: false);
            return;
        }

        if (_current == null || _current.IsWebLink ||
            (_player.State != RadioState.Playing && _player.State != RadioState.Connecting))
        {
            ShowMessage("Start playing a station first, then press Record.", isError: true);
            return;
        }

        if (!RecordingNoticeWindow.EnsureConsent(this))
        {
            ShowMessage("Recording cancelled. You can read the notice again under Settings.", isError: false);
            return;
        }

        var station = _current;
        try
        {
            var task = _recorder.StartAsync(station, _player.CurrentUrl == station.StreamUrl ? _player.CurrentMrl : null);
            UpdateRecordUi();
            UpdateControls();
            var path = await task;
            if (path == null || _closing) { UpdateRecordUi(); UpdateControls(); return; }
            _recordTimer.Start();
            ShowMessage("Recording to " + path, isError: false);
        }
        catch (Exception ex)
        {
            Storage.Log(ex, "Start recording");
            ShowMessage("Sorry, recording couldn't start (" + ex.Message + ").", isError: true);
        }
        UpdateRecordUi();
        UpdateControls();
    }

    private async Task StopRecordingAsync(string doneMessage, bool isError)
    {
        if (!_recorder.IsRecording) return;
        _recordTimer.Stop();
        var (path, bytes) = await _recorder.StopAsync();
        if (_closing) return;
        UpdateRecordUi();
        UpdateControls();
        if (path == null && bytes == 0)
        {
            if (isError) ShowMessage(doneMessage, isError: true);
            return;
        }
        if (bytes > 0 && path != null)
            ShowMessage($"{doneMessage}  {System.IO.Path.GetFileName(path)}  ({bytes / 1024.0 / 1024.0:0.0} MB) in {Storage.RecordingsFolder}", isError);
        else
            ShowMessage(isError ? doneMessage + " Nothing was saved." : "Nothing was recorded - the station may not allow it.", isError: true);
    }

    private void UpdateRecordUi()
    {
        if (_recorder.IsRecording)
        {
            var t = DateTime.Now - _recorder.StartedAt;
            if (t < TimeSpan.Zero || _recorder.FilePath == null) t = TimeSpan.Zero;
            RecordButtonText.Text = $"Stop recording  {(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
            RecordDot.Opacity = RecordDot.Opacity > 0.5 ? 0.3 : 1.0; // gentle blink
            AutomationProperties.SetName(RecordButton, "Stop recording");
        }
        else
        {
            RecordButtonText.Text = "Record";
            RecordDot.Opacity = 1.0;
            AutomationProperties.SetName(RecordButton, "Record");
        }
    }

    private void OpenRecordingsButton_Click(object sender, RoutedEventArgs e) => OpenFolder(Storage.RecordingsFolder, "recordings folder");

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Storage.DataFolder, "data folder");

    private void OpenFolder(string folder, string what)
    {
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Storage.Log(ex, "Open " + what);
            ShowMessage($"Couldn't open the {what}: {folder}", isError: true);
        }
    }

    // ------------------------------------------------------------------ Radio Browser search

    private async Task LoadCountriesAsync()
    {
        try
        {
            var countries = await _api.GetCountriesAsync(_lifetime.Token);
            if (countries.Count == 0 || _closing) return;
            var wanted = (CountryBox.SelectedItem as CountryOption)?.Code ?? "GB";
            var items = new List<CountryOption> { new() { Code = "", Name = "Any country" } };
            items.AddRange(countries);
            _suppressAutoSearch = true;
            CountryBox.ItemsSource = items;
            CountryBox.SelectedItem = items.FirstOrDefault(c => c.Code == wanted) ?? items.FirstOrDefault(c => c.Code == "GB") ?? items[0];
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Storage.Log(ex, "Loading countries"); // keep the small built-in list
        }
    }

    private async Task SearchAsync()
    {
        if (_closing) return;
        var old = _searchCts;
        old?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _searchCts = cts;

        var country = CountryBox.SelectedItem as CountryOption;
        var sort = SortBox.SelectedItem as SortOption ?? new SortOption { Label = "Most popular" };
        _settings.CountryCode = country?.Code ?? "";
        _settings.SortOrder = sort.Order;

        _results.Clear();
        ResultsEmptyText.Text = "Searching for stations…";
        ResultsEmptyText.Visibility = Visibility.Visible;
        ResultsCountText.Text = "";
        SearchButton.IsEnabled = false;
        try
        {
            var list = await _api.SearchAsync(NameBox.Text, country?.Code, TagBox.Text, sort, 300, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var s in list.Where(s => StreamUrl.IsPlayable(s.StreamUrl))) _results.Add(s);
            if (_results.Count == 0)
            {
                ResultsEmptyText.Text = "No stations found. Try a shorter name, a different genre, or 'Any country'.";
            }
            else
            {
                ResultsEmptyText.Visibility = Visibility.Collapsed;
                ResultsCountText.Text = $"{_results.Count} stations  ·  double-click to play";
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Storage.Log(ex, "Search");
            ResultsEmptyText.Text = "Couldn't reach the station directory.\nPlease check your internet connection and press Search to try again.";
            ResultsEmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) { SearchButton.IsEnabled = true; }
            old?.Dispose();
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = SearchAsync(); }
    }

    private void PlaySelectedResult_Click(object sender, RoutedEventArgs e) =>
        PlayOrHint(ResultsList.SelectedItem as Station);

    private void AddSelectedResultToFavourites_Click(object sender, RoutedEventArgs e) =>
        AddFavouriteOrHint(ResultsList.SelectedItem as Station);

    // ------------------------------------------------------------------ Scanner

    private void LoadScannerList()
    {
        var result = ScannerRepository.Load();
        _scannerAll = result.Feeds;

        var categories = _scannerAll.Select(s => s.Category).Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => Array.IndexOf(CategoryOrder, c) is var i && i >= 0 ? i : 100).ThenBy(c => c).ToList();
        var previous = ScannerCategoryBox.SelectedItem as string;
        categories.Insert(0, AnyCategory);
        ScannerCategoryBox.ItemsSource = categories;
        ScannerCategoryBox.SelectedItem = previous != null && categories.Contains(previous) ? previous : AnyCategory;

        ApplyScannerFilter();
        if (result.Problem != null) ShowMessage(result.Problem, isError: true);
    }

    private void ApplyScannerFilter()
    {
        if (ScannerList == null || ScannerCategoryBox == null || ScannerFilterBox == null) return;
        var text = ScannerFilterBox.Text?.Trim() ?? "";
        var cat = ScannerCategoryBox.SelectedItem as string ?? AnyCategory;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        bool Matches(Station s)
        {
            if (cat != AnyCategory && !string.Equals(s.Category, cat, StringComparison.OrdinalIgnoreCase)) return false;
            var hay = $"{s.Name} {s.Region} {s.Notes} {s.Category} {s.StreamUrl}";
            return words.All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase));
        }

        _scannerView.Clear();
        foreach (var s in _scannerAll.Where(Matches)) _scannerView.Add(s);
        int live = _scannerView.Count(s => !s.IsWebLink);
        ScannerCountText.Text = $"{_scannerView.Count} feeds ({live} play here, {_scannerView.Count - live} open in browser)";
        ScannerEmptyText.Text = _scannerAll.Count == 0
            ? "No scanner feeds found. Press 'Edit list' to add some."
            : _scannerView.Count == 0 ? "Nothing matches your filter." : "";
    }

    private void ScannerFilter_Changed(object sender, RoutedEventArgs e) => ApplyScannerFilter();

    private void PlaySelectedScanner_Click(object sender, RoutedEventArgs e) =>
        PlayOrHint(ScannerList.SelectedItem as Station);

    private void AddSelectedScannerToFavourites_Click(object sender, RoutedEventArgs e) =>
        AddFavouriteOrHint(ScannerList.SelectedItem as Station);

    private void EditScannerList_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ScannerRepository.EnsureUserCopy();
            try
            {
                // The user's preferred editor for .json, if one is registered ...
                Process.Start(new ProcessStartInfo(Storage.ScannerPath) { UseShellExecute = true, Verb = "edit" });
            }
            catch
            {
                // ... otherwise Notepad.
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Storage.ScannerPath}\"") { UseShellExecute = true });
            }
            ShowMessage("Edit the list, save it, then press Reload.", isError: false);
        }
        catch (Exception ex)
        {
            Storage.Log(ex, "Open scanner list");
            ShowMessage("Couldn't open the list. It is here: " + Storage.ScannerPath, isError: true);
        }
    }

    private void ReloadScannerList_Click(object sender, RoutedEventArgs e)
    {
        HideMessage();
        LoadScannerList();
    }

    // ------------------------------------------------------------------ Favourites

    private bool IsFavourite(Station? s) => s != null && _favourites.Any(f => f.Key == s.Key);

    private void SaveFavourites()
    {
        if (!Storage.SaveFavourites(_favourites, userEdited: true))
            ShowMessage("Couldn't save your favourites. Is the disk full or the file read-only? " + Storage.FavouritesPath, isError: true);
    }

    private void AddFavouriteOrHint(Station? s)
    {
        if (s == null) { ShowMessage("Click a station in the list first.", isError: true); return; }
        if (IsFavourite(s)) { ShowMessage($"{s.DisplayName} is already in your favourites.", isError: false); return; }
        _favourites.Add(s.Clone());
        SaveFavourites();
        ShowMessage($"Added {s.DisplayName} to your favourites.", isError: false);
    }

    private void FavouriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) { ShowMessage("Play a station first.", isError: true); return; }
        var existing = _favourites.FirstOrDefault(f => f.Key == _current.Key);
        if (existing != null)
        {
            _favourites.Remove(existing);
            SaveFavourites();
            ShowMessage($"Removed {_current.DisplayName} from your favourites.", isError: false);
        }
        else
        {
            AddFavouriteOrHint(_current);
        }
    }

    private void PlaySelectedFavourite_Click(object sender, RoutedEventArgs e) =>
        PlayOrHint(FavouritesList.SelectedItem as Station);

    private void RemoveFavourite_Click(object sender, RoutedEventArgs e)
    {
        if (FavouritesList.SelectedItem is not Station s) { ShowMessage("Click a favourite first.", isError: true); return; }
        int index = FavouritesList.SelectedIndex;
        _favourites.Remove(s);
        SaveFavourites();
        if (_favourites.Count > 0) FavouritesList.SelectedIndex = Math.Min(index, _favourites.Count - 1);
    }

    private void MoveFavourite_Click(object sender, RoutedEventArgs e)
    {
        if (FavouritesList.SelectedItem is not Station s) { ShowMessage("Click a favourite first.", isError: true); return; }
        int dir = int.TryParse((sender as FrameworkElement)?.Tag?.ToString(), out var d) ? d : 0;
        int from = _favourites.IndexOf(s), to = from + dir;
        if (from < 0 || to < 0 || to >= _favourites.Count) return;
        _favourites.Move(from, to);
        FavouritesList.SelectedIndex = to;
        SaveFavourites();
    }

    private void UpdateFavouritesUi()
    {
        FavouritesEmptyText.Visibility = _favourites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FavouritesCountText.Text = _favourites.Count == 0 ? "" : $"{_favourites.Count} favourites";
        UpdateControls();
    }

    // ------------------------------------------------------------------ History

    private void UpdateHistoryUi()
    {
        HistoryEmptyText.Visibility = _history.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryCountText.Text = _history.Items.Count == 0 ? "" : $"{_history.Items.Count} of the last {HistoryStore.MaxEntries} kept";
    }

    private List<HistoryEntry> SelectedHistory() =>
        HistoryList.SelectedItems.OfType<HistoryEntry>().OrderByDescending(h => h.Time).ToList();

    private void CopyToClipboard(IEnumerable<HistoryEntry> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0) { ShowMessage("Click one or more lines first.", isError: true); return; }
        var text = string.Join(Environment.NewLine, list.Select(h => h.Line));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                ShowMessage(list.Count == 1 ? "Copied: " + list[0].Title : $"Copied {list.Count} lines.", isError: false);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(50); } // clipboard busy in another app
            catch (Exception ex) { Storage.Log(ex, "Clipboard"); break; }
        }
        ShowMessage("Couldn't copy - another program is using the clipboard. Please try again.", isError: true);
    }

    private void CopyHistorySelected_Click(object sender, RoutedEventArgs e) => CopyToClipboard(SelectedHistory());

    private void CopyHistoryAll_Click(object sender, RoutedEventArgs e) => CopyToClipboard(_history.Items);

    private void SearchHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryEntry h) { ShowMessage("Click a line first.", isError: true); return; }
        OpenInBrowser("https://duckduckgo.com/?q=" + Uri.EscapeDataString(h.Title));
    }

    private void RemoveHistory_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedHistory();
        if (sel.Count == 0) { ShowMessage("Click one or more lines first.", isError: true); return; }
        _history.Remove(sel);
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Items.Count == 0) return;
        if (MessageBox.Show(this, "Clear the whole now-playing history?", "Mighty Radio",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            _history.Clear();
    }

    private void HistoryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; CopyToClipboard(SelectedHistory()); }
        else if (e.Key == Key.Delete) { e.Handled = true; RemoveHistory_Click(sender, e); }
    }

    // ------------------------------------------------------------------ Schedule

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return; // ignore bubbling from inner ComboBoxes/ListBoxes
        if (Tabs.SelectedItem is TabItem { Header: "Schedule" }) RefreshScheduleForm();
    }

    private void RefreshScheduleForm()
    {
        var prevStation = ScheduleStationBox.SelectedItem as Station;
        var stations = _favourites.Where(f => !f.IsWebLink).ToList();
        if (_current != null && !_current.IsWebLink && stations.All(s => s.Key != _current.Key)) stations.Insert(0, _current);
        ScheduleStationBox.ItemsSource = stations;
        ScheduleStationBox.SelectedItem = stations.FirstOrDefault(s => s.Key == prevStation?.Key)
                                          ?? stations.FirstOrDefault(s => s.Key == _current?.Key) ?? stations.FirstOrDefault();

        var prevDay = (ScheduleDayBox.SelectedItem as DayOption)?.Date;
        var today = DateTime.Today;
        var days = new List<DayOption> { new("Today", today), new("Tomorrow", today.AddDays(1)) };
        for (int i = 2; i < 8; i++) days.Add(new(today.AddDays(i).ToString("dddd d MMM"), today.AddDays(i)));
        ScheduleDayBox.ItemsSource = days;
        ScheduleDayBox.SelectedItem = days.FirstOrDefault(d => d.Date == prevDay) ?? days[0];

        ScheduleHintText.Text = stations.Count == 0
            ? "Add a station to your favourites (or play one) to schedule it."
            : $"Recordings run while Mighty Radio is open - minimised or in the notification area is fine, but not when the PC is asleep. Files go to {Storage.RecordingsFolder}.";
    }

    private void AddSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleStationBox.SelectedItem is not Station station) { ShowMessage("Choose a station to record.", isError: true); return; }
        if (ScheduleDayBox.SelectedItem is not DayOption day) return;
        var repeat = (ScheduleRepeatBox.SelectedItem as RepeatOption)?.Repeat ?? ScheduleRepeat.Once;

        var timeText = ScheduleTimeBox.Text.Trim().Replace('.', ':');
        if (!TimeSpan.TryParseExact(timeText, new[] { @"h\:mm", @"hh\:mm", "hhmm" }, null, out var time) || time >= TimeSpan.FromDays(1))
        {
            ShowMessage("Type the start time as hours:minutes in 24-hour time, for example 07:30 or 19:00.", isError: true);
            return;
        }
        if (!int.TryParse(ScheduleMinutesBox.Text.Trim(), out var minutes) || minutes < 1 || minutes > 24 * 60)
        {
            ShowMessage("Type how long to record in minutes (1 to 1440).", isError: true);
            return;
        }
        if (!StreamUrl.IsPlayable(station.StreamUrl)) { ShowMessage("That station can't be recorded.", isError: true); return; }

        var start = day.Date + time;
        if (repeat == ScheduleRepeat.Once && start.AddMinutes(minutes) <= DateTime.Now)
        {
            ShowMessage("That time has already passed. Pick a later time or another day.", isError: true);
            return;
        }

        // Scheduled recordings can't start until the recording notice has been accepted.
        if (!RecordingNoticeWindow.EnsureConsent(this))
        {
            ShowMessage("Not scheduled: the recording notice needs to be accepted first.", isError: false);
            return;
        }

        var entry = new ScheduleEntry
        {
            Station = station.Clone(), Start = start, DurationMinutes = minutes, Repeat = repeat, Enabled = true,
        };
        _scheduler.Add(entry);
        ScheduleList.SelectedItem = entry;
        var next = entry.NextOccurrence(DateTime.Now);
        ShowMessage(next != null
            ? $"Scheduled: {station.DisplayName}, {Scheduler.Friendly(next.Value)} for {minutes} min."
            : $"Scheduled: {station.DisplayName}.", isError: false);
    }

    private async void RemoveSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleList.SelectedItem is not ScheduleEntry s) { ShowMessage("Click a scheduled recording first.", isError: true); return; }
        if (s.IsRunning && MessageBox.Show(this, "This recording is in progress. Stop it and remove the schedule?", "Mighty Radio",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { await _scheduler.RemoveAsync(s); }
        catch (Exception ex) { Storage.Log(ex, "Remove schedule"); }
    }

    private async void ToggleSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleList.SelectedItem is not ScheduleEntry s) { ShowMessage("Click a scheduled recording first.", isError: true); return; }
        if (!s.Enabled && s.Repeat == ScheduleRepeat.Once && s.NextOccurrence(DateTime.Now) == null)
        {
            ShowMessage("That one-off recording time has passed. Add a new schedule instead.", isError: true);
            return;
        }
        try { await _scheduler.SetEnabledAsync(s, !s.Enabled); }
        catch (Exception ex) { Storage.Log(ex, "Pause schedule"); }
    }

    private void OnSchedulerMessage(string text, bool isError)
    {
        if (_closing) return;
        ShowMessage(text, isError);
        if (_hiddenToTray) _tray.Notify("Mighty Radio", text);
        UpdateTray();
    }

    private void UpdateScheduleStatus()
    {
        var text = _scheduler.StatusText();
        NextRecordingText.Text = text;
        bool show = _scheduler.IsRecording || _scheduler.NextUp() != null;
        ScheduleStatusText.Text = _scheduler.IsRecording ? "● REC (scheduled)" : (show ? "⏲ " + text.Replace("Next recording: ", "") : "");
        ScheduleStatusText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ScheduleEmptyText.Visibility = _scheduler.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ Settings

    private void SelectThemeInBoxes(string id)
    {
        var opt = ThemeManager.Themes.FirstOrDefault(t => t.Id == id) ?? ThemeManager.Themes[0];
        bool old = _suppressUiEvents;
        _suppressUiEvents = true;
        ThemeBox.SelectedItem = opt;
        QuickThemeBox.SelectedItem = opt;
        _suppressUiEvents = old;
        ThemeDescriptionText.Text = opt.Description;
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || (sender as ComboBox)?.SelectedItem is not ThemeOption opt) return;
        // TryApply checks the theme off-screen and keeps the current one if it fails.
        var problem = ThemeManager.TryApply(opt.Id);
        if (problem == null)
        {
            _settings.Theme = ThemeManager.Current;
            Storage.SaveSettings(_settings);
        }
        else
        {
            ShowMessage($"Sorry, the {opt.Name} theme couldn't be loaded, so the {ThemeManager.Name(ThemeManager.Current)} theme is still in use. " +
                        $"Details are in {Storage.LogPath}.", isError: true);
        }
        SelectThemeInBoxes(ThemeManager.Current);
    }

    /// <summary>A message in the now-playing panel, for App (e.g. a theme fallback at start-up).</summary>
    public void ShowNotice(string text, bool isError) => ShowMessage(text, isError);

    /// <summary>Exit for real (not to the tray), going through the normal shutdown that stops VLC.</summary>
    public void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    private void TrayBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        _settings.MinimiseToTray = TrayBox.IsChecked == true;
        _settings.CloseToTrayWhenScheduled = CloseToTrayBox.IsChecked == true;
        Storage.SaveSettings(_settings);
    }

    private void ViewNotice_Click(object sender, RoutedEventArgs e) => RecordingNoticeWindow.ShowNotice(this);

    private void OpenProjectPage_Click(object sender, RoutedEventArgs e) => OpenInBrowser(AppInfo.RepoUrl);

    // ------------------------------------------------------------------ Shared helpers

    private void PlayOrHint(Station? s)
    {
        if (s == null) { ShowMessage("Click a station in the list first.", isError: true); return; }
        Play(s);
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || e.OriginalSource is not DependencyObject src) return;
        if (ItemsControl.ContainerFromElement(list, src) is ListBoxItem item && item.DataContext is Station s)
            Play(s);
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (e.Key == Key.Enter && list.SelectedItem is Station s) { e.Handled = true; Play(s); }
        else if (e.Key == Key.Delete && ReferenceEquals(list, FavouritesList)) { e.Handled = true; RemoveFavourite_Click(sender, e); }
    }

    private void OpenInBrowser(string url)
    {
        if (!StreamUrl.TryGetPlayable(url, out var uri))
        {
            ShowMessage("That link doesn't look right: " + url, isError: true);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            ShowMessage("Opened in your web browser.", isError: false);
        }
        catch (Exception ex)
        {
            Storage.Log(ex, "Open browser");
            ShowMessage("Couldn't open your web browser.", isError: true);
        }
    }

    private void UpdateControls()
    {
        if (_player == null || PlayButton == null) return;
        bool active = _player.State is RadioState.Playing or RadioState.Connecting;
        bool recording = _recorder?.IsRecording == true;
        PlayButton.IsEnabled = _current != null;
        StopButton.IsEnabled = active || recording;
        RecordButton.IsEnabled = recording || (active && _current is { IsWebLink: false });
        FavouriteButton.IsEnabled = _current != null;
        bool fav = IsFavourite(_current);
        FavouriteButtonText.Text = fav ? "★  In favourites" : "☆  Add to favourites";
        AutomationProperties.SetName(FavouriteButton, fav ? "Remove from favourites" : "Add to favourites");
        if (_player.State == RadioState.Stopped && _current == null) StatusLabel.Text = "READY";
    }

    private void ShowMessage(string text, bool isError)
    {
        MessageText.Text = text;
        MessageText.SetResourceReference(TextBlock.ForegroundProperty, isError ? "ErrorBrush" : "OkBrush");
        MessageText.Visibility = Visibility.Visible;
        Announce(MessageText);
    }

    private void HideMessage() => MessageText.Visibility = Visibility.Collapsed;

    private static void Announce(UIElement element)
    {
        try
        {
            var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch { /* screen-reader notifications are best effort */ }
    }

    // ------------------------------------------------------------------ Closing

    private void SaveState()
    {
        try
        {
            _settings.Volume = _player.Volume;
            if (WindowState == WindowState.Normal && !_miniMode && IsVisible)
            {
                _settings.WindowWidth = ActualWidth;
                _settings.WindowHeight = ActualHeight;
            }
            _settings.Theme = ThemeManager.Current;
            Storage.SaveSettings(_settings);
            _history.Flush();
            // Favourites are saved whenever they change; never on exit (a failed read must not wipe the file).
        }
        catch (Exception ex) { Storage.Log(ex, "Saving state"); }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownDone) return;
        e.Cancel = true;
        if (_closing) return;

        // Keep running for scheduled recordings unless the user really asked to exit.
        if (!_exitRequested && _settings.CloseToTrayWhenScheduled && (_scheduler.HasPending || _scheduler.IsRecording))
        {
            SaveState();
            HideToTray(notify: true);
            return;
        }

        _closing = true;
        SaveState();
        Hide();
        _tray.Visible = false;
        _lifetime.Cancel();
        _searchCts?.Cancel();
        _recordTimer.Stop();
        _sleepTimer.Stop();

        bool vlcIdle = false;
        try
        {
            var schedulerDone = _scheduler.ShutdownAsync();
            var recorderDone = _recorder.ShutdownAsync(TimeSpan.FromSeconds(3));
            var playerDone = _player.ShutdownAsync(TimeSpan.FromSeconds(3));
            var all = Task.WhenAll(schedulerDone, recorderDone, playerDone);
            if (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(6))) == all)
            {
                await all;
                // false = VLC didn't stop in time; then LibVLC must not be disposed (it could hang or crash).
                vlcIdle = recorderDone.Result && playerDone.Result;
            }
        }
        catch (Exception ex) { Storage.Log(ex, "Shutdown"); }

        try { _media.Dispose(); } catch { }
        try { _tray.Dispose(); } catch { }
        _shutdownDone = true;
        (Application.Current as App)?.FinishShutdown(vlcIdle);
    }
}
