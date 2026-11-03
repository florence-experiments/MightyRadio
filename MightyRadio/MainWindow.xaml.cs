using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MightyRadio.Models;
using MightyRadio.Services;
using Brush = System.Windows.Media.Brush;

namespace MightyRadio;

public partial class MainWindow : Window
{
    private const string AnyCategory = "All categories";
    private static readonly string[] CategoryOrder =
    {
        "Air / ATC", "Marine / VHF", "Space", "Public safety", "Amateur / ham", "Weather", "Numbers / shortwave / utility", "Rail",
    };

    private readonly RadioPlayer _player;
    private readonly Recorder _recorder;
    private readonly RadioBrowserClient _api = new();
    private readonly AppSettings _settings;
    private readonly ObservableCollection<Station> _results = new();
    private readonly ObservableCollection<Station> _favourites;
    private readonly ObservableCollection<Station> _scannerView = new();
    private readonly DispatcherTimer _recordTimer;
    private List<Station> _scannerAll = new();
    private CancellationTokenSource? _searchCts;
    private Station? _current;
    private bool _suppressAutoSearch = true;
    private bool _closing;

    public MainWindow(RadioPlayer player)
    {
        _player = player; // assigned before InitializeComponent: XAML may raise ValueChanged early
        _settings = Storage.LoadSettings();
        _favourites = new ObservableCollection<Station>(Storage.LoadFavourites().Where(f => !string.IsNullOrWhiteSpace(f.StreamUrl)));
        InitializeComponent();

        _recorder = new Recorder(_player.LibVlc, Dispatcher);
        _recorder.Failed += msg => _ = StopRecordingAsync(msg, isError: true);
        _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recordTimer.Tick += (_, _) => UpdateRecordUi();

        if (_settings.WindowWidth >= MinWidth && _settings.WindowWidth < 5000) Width = _settings.WindowWidth;
        if (_settings.WindowHeight >= MinHeight && _settings.WindowHeight < 4000) Height = _settings.WindowHeight;

        ResultsList.ItemsSource = _results;
        FavouritesList.ItemsSource = _favourites;
        ScannerList.ItemsSource = _scannerView;

        _player.StateChanged += OnPlayerStateChanged;
        _player.NowPlayingChanged += text => NowPlayingText.Text = text ?? "";
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

        _favourites.CollectionChanged += (_, _) => { UpdateFavouritesUi(); };
        UpdateFavouritesUi();
        LoadScannerList();

        if (_settings.LastStation is { } last && !string.IsNullOrWhiteSpace(last.StreamUrl))
        {
            _current = last;
            StationNameText.Text = last.DisplayName;
            NowPlayingText.Text = "Press Play to listen";
        }
        UpdateControls();

        Loaded += async (_, _) =>
        {
            await LoadCountriesAsync();
            _suppressAutoSearch = false;
            await SearchAsync();
        };
        Closing += OnClosing;
    }

    // ------------------------------------------------------------------ Playback

    private async void Play(Station? station)
    {
        if (station == null) return;
        if (station.IsWebLink)
        {
            OpenInBrowser(station.StreamUrl);
            return;
        }

        if (_recorder.IsRecording)
            await StopRecordingAsync("Recording saved (you changed station).", isError: false);

        _current = station;
        StationNameText.Text = station.DisplayName;
        NowPlayingText.Text = "";
        HideMessage();
        _player.Play(station.StreamUrl);
        if (!station.IsScanner) _api.ReportClick(station.StationUuid);

        _settings.LastStation = station;
        Storage.SaveSettings(_settings);
        UpdateControls();
    }

    private void OnPlayerStateChanged(RadioState state, string? message)
    {
        switch (state)
        {
            case RadioState.Connecting:
                StatusLabel.Text = "CONNECTING…";
                NowPlayingText.Text = "Tuning in…";
                break;
            case RadioState.Playing:
                StatusLabel.Text = "● LIVE";
                if (NowPlayingText.Text == "Tuning in…") NowPlayingText.Text = "";
                HideMessage();
                break;
            case RadioState.Stopped:
                StatusLabel.Text = "STOPPED";
                NowPlayingText.Text = "";
                break;
            case RadioState.Error:
                StatusLabel.Text = "NOT AVAILABLE";
                NowPlayingText.Text = "";
                ShowMessage(message ?? "Sorry, something went wrong with this station.", isError: true);
                break;
        }

        if ((state == RadioState.Error || state == RadioState.Stopped) && _recorder.IsRecording)
            _ = StopRecordingAsync("Recording saved.", isError: false);

        UpdateControls();
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
        if (_recorder.IsRecording) await StopRecordingAsync("Recording saved.", isError: false);
        _player.Stop();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player == null) return;
        _player.Volume = (int)Math.Round(e.NewValue);
        if (VolumeText != null) VolumeText.Text = _player.Volume.ToString();
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

        try
        {
            var path = _recorder.Start(_current);
            _recordTimer.Start();
            ShowMessage("Recording to " + path, isError: false);
        }
        catch (Exception ex)
        {
            Storage.Log(ex, "Start recording");
            ShowMessage("Sorry, recording couldn't start (" + ex.Message + ").", isError: true);
        }
        UpdateRecordUi();
    }

    private async Task StopRecordingAsync(string doneMessage, bool isError)
    {
        if (!_recorder.IsRecording) return;
        _recordTimer.Stop();
        var (path, bytes) = await _recorder.StopAsync();
        if (_closing) return;
        UpdateRecordUi();
        if (path == null) return;
        if (bytes > 0)
            ShowMessage($"{doneMessage}  {System.IO.Path.GetFileName(path)}  ({bytes / 1024.0 / 1024.0:0.0} MB) in Music\\MightyRadio", isError);
        else
            ShowMessage(isError ? doneMessage + " Nothing was saved." : "Nothing was recorded - the station may not allow it.", isError: true);
    }

    private void UpdateRecordUi()
    {
        if (_recorder.IsRecording)
        {
            var t = DateTime.Now - _recorder.StartedAt;
            RecordButtonText.Text = $"Stop recording  {(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
            RecordDot.Opacity = RecordDot.Opacity > 0.5 ? 0.3 : 1.0; // gentle blink
        }
        else
        {
            RecordButtonText.Text = "Record";
            RecordDot.Opacity = 1.0;
        }
    }

    private void OpenRecordingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Storage.RecordingsFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Storage.RecordingsFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Storage.Log(ex, "Open recordings folder");
            ShowMessage("Couldn't open the recordings folder: " + Storage.RecordingsFolder, isError: true);
        }
    }

    // ------------------------------------------------------------------ Radio Browser search

    private async Task LoadCountriesAsync()
    {
        try
        {
            var countries = await _api.GetCountriesAsync(CancellationToken.None);
            if (countries.Count == 0) return;
            var wanted = (CountryBox.SelectedItem as CountryOption)?.Code ?? "GB";
            var items = new List<CountryOption> { new() { Code = "", Name = "Any country" } };
            items.AddRange(countries);
            _suppressAutoSearch = true;
            CountryBox.ItemsSource = items;
            CountryBox.SelectedItem = items.FirstOrDefault(c => c.Code == wanted) ?? items.FirstOrDefault(c => c.Code == "GB") ?? items[0];
        }
        catch (Exception ex)
        {
            Storage.Log(ex, "Loading countries"); // keep the small built-in list
        }
    }

    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
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
            foreach (var s in list.Where(s => !string.IsNullOrWhiteSpace(s.StreamUrl))) _results.Add(s);
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
            if (ReferenceEquals(_searchCts, cts)) SearchButton.IsEnabled = true;
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

        var categories = _scannerAll.Select(s => s.Category).Distinct(StringComparer.OrdinalIgnoreCase)
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
        if (ScannerList == null || ScannerCategoryBox == null) return;
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
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Storage.ScannerPath}\"") { UseShellExecute = true });
            ShowMessage("Edit the list in Notepad, save it, then press Reload.", isError: false);
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

    private void AddFavouriteOrHint(Station? s)
    {
        if (s == null) { ShowMessage("Click a station in the list first.", isError: true); return; }
        if (IsFavourite(s)) { ShowMessage($"{s.DisplayName} is already in your favourites.", isError: false); return; }
        _favourites.Add(s);
        Storage.SaveFavourites(_favourites);
        ShowMessage($"Added {s.DisplayName} to your favourites.", isError: false);
    }

    private void FavouriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) { ShowMessage("Play a station first.", isError: true); return; }
        var existing = _favourites.FirstOrDefault(f => f.Key == _current.Key);
        if (existing != null)
        {
            _favourites.Remove(existing);
            Storage.SaveFavourites(_favourites);
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
        Storage.SaveFavourites(_favourites);
        if (_favourites.Count > 0) FavouritesList.SelectedIndex = Math.Min(index, _favourites.Count - 1);
    }

    private void MoveFavourite_Click(object sender, RoutedEventArgs e)
    {
        if (FavouritesList.SelectedItem is not Station s) return;
        int dir = int.TryParse((sender as FrameworkElement)?.Tag?.ToString(), out var d) ? d : 0;
        int from = _favourites.IndexOf(s), to = from + dir;
        if (from < 0 || to < 0 || to >= _favourites.Count) return;
        _favourites.Move(from, to);
        FavouritesList.SelectedIndex = to;
        Storage.SaveFavourites(_favourites);
    }

    private void UpdateFavouritesUi()
    {
        FavouritesEmptyText.Visibility = _favourites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FavouritesCountText.Text = _favourites.Count == 0 ? "" : $"{_favourites.Count} favourites";
        UpdateControls();
    }

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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
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
        bool active = _player.State is RadioState.Playing or RadioState.Connecting;
        PlayButton.IsEnabled = _current != null;
        StopButton.IsEnabled = active || _recorder?.IsRecording == true;
        RecordButton.IsEnabled = (_recorder?.IsRecording == true) || (active && _current is { IsWebLink: false });
        FavouriteButton.IsEnabled = _current != null;
        FavouriteButtonText.Text = IsFavourite(_current) ? "★  In favourites" : "☆  Add to favourites";
        if (_player.State == RadioState.Stopped && _current == null) StatusLabel.Text = "READY";
    }

    private void ShowMessage(string text, bool isError)
    {
        MessageText.Text = text;
        MessageText.Foreground = (Brush)FindResource(isError ? "ErrorBrush" : "OkBrush");
        MessageText.Visibility = Visibility.Visible;
    }

    private void HideMessage() => MessageText.Visibility = Visibility.Collapsed;

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        try
        {
            _settings.Volume = _player.Volume;
            if (WindowState == WindowState.Normal)
            {
                _settings.WindowWidth = ActualWidth;
                _settings.WindowHeight = ActualHeight;
            }
            Storage.SaveSettings(_settings);
            Storage.SaveFavourites(_favourites);
        }
        catch (Exception ex) { Storage.Log(ex, "Saving on exit"); }

        _searchCts?.Cancel();
        _recordTimer.Stop();
        _recorder.Dispose();   // finalises any recording in progress
        _player.Dispose();
        _api.Dispose();
    }
}
