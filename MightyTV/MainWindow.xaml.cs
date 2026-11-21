using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LibVLCSharp.Shared;
using Mighty.Directory;
using MightyTV.Services;

namespace MightyTV;

public partial class MainWindow : Window
{
    private readonly TvPlayer _player;
    private readonly CancellationTokenSource _loadCts = new();
    private readonly IptvOrgClient _guides = new();
    private Task<GuideDirectory>? _guideIndex;
    private int _guideRequest;
    private ChannelCatalog? _catalog;
    private bool _suppressFilter;
    private bool _fullscreen;
    private WindowStyle _savedStyle;
    private WindowState _savedState;
    private GridLength _savedVideoHeight;
    private GridLength _savedBrowserHeight;

    public bool PlayerIdle { get; private set; }

    public MainWindow(LibVLC vlc)
    {
        InitializeComponent();
        _player = new TvPlayer(Dispatcher, vlc);
        _player.StateChanged += OnPlaybackState;
        Video.MediaPlayer = _player.MediaPlayer;
        _player.Volume = (int)VolumeSlider.Value;
        Loaded += (_, _) => _ = LoadDirectoryAsync();
    }

    private async Task LoadDirectoryAsync()
    {
        try
        {
            using var client = new IptvOrgClient();
            _catalog = await client.LoadAsync(_loadCts.Token);
            if (!IsLoaded) return;
            FillFilters();
            StatusLabel.Text = "READY";
            ChannelNameText.Text = "Choose a channel below";
        }
        catch (OperationCanceledException) when (_loadCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsLoaded) return;
            StatusLabel.Text = "ERROR";
            ChannelNameText.Text = "Channel directory unavailable";
            ShowMessage("Couldn't load channels from iptv-org. " + ex.Message);
        }
    }

    private void FillFilters()
    {
        if (_catalog == null) return;
        _suppressFilter = true;

        CountryBox.Items.Clear();
        CountryBox.Items.Add(new CountryOption(null, "Any country"));
        CountryOption? uk = null;
        foreach (var country in _catalog.Countries)
        {
            CountryBox.Items.Add(country);
            if (string.Equals(country.Code, ChannelCatalog.UkCountryCode, StringComparison.OrdinalIgnoreCase))
                uk = country;
        }
        CountryBox.SelectedItem = uk ?? CountryBox.Items[0];

        CategoryBox.Items.Clear();
        CategoryBox.Items.Add(new CategoryOption(null, "Any category"));
        foreach (var category in _catalog.Categories)
            CategoryBox.Items.Add(category);
        CategoryBox.SelectedIndex = 0;

        _suppressFilter = false;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_catalog == null || _suppressFilter) return;
        var country = (CountryBox.SelectedItem as CountryOption)?.Code;
        var category = (CategoryBox.SelectedItem as CategoryOption)?.Id;
        var list = _catalog.Filter(NameBox.Text, country, category, AdultBox.IsChecked == true);
        ChannelList.ItemsSource = list;
        CountText.Text = list.Count == 1 ? "1 channel" : $"{list.Count} channels";
    }

    private void PlaySelected()
    {
        if (ChannelList.SelectedItem is not ChannelListing channel)
        {
            ShowMessage("Choose a channel first.");
            return;
        }
        ChannelNameText.Text = channel.Name;
        ShowMessage(null);
        _player.Play(channel.StreamUrl, channel.UserAgent, channel.Referrer);
    }

    private void OnPlaybackState(PlaybackState state, string? message)
    {
        StatusLabel.Text = state switch
        {
            PlaybackState.Connecting => "CONNECTING",
            PlaybackState.Playing => "PLAYING",
            PlaybackState.Error => "ERROR",
            _ => "READY",
        };
        ShowMessage(state == PlaybackState.Error ? message ?? "This channel isn't working right now." : null);
    }

    private void ShowMessage(string? text)
    {
        MessageText.Text = text ?? "";
        MessageText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => PlaySelected();
    private void StopButton_Click(object sender, RoutedEventArgs e) => _player.Stop();
    private void SearchButton_Click(object sender, RoutedEventArgs e) => ApplyFilter();
    private void AdultBox_Changed(object sender, RoutedEventArgs e) => ApplyFilter();
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyFilter();
    }

    private async void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var request = ++_guideRequest;
        SetGuide(null);
        if (ChannelList.SelectedItem is not ChannelListing channel || channel.Id.StartsWith("stream:", StringComparison.Ordinal))
            return;
        try
        {
            _guideIndex ??= _guides.LoadGuidesAsync(_loadCts.Token);
            var index = await _guideIndex;
            if (request != _guideRequest) return;
            var link = index.Find(channel.Id);
            if (link == null) return;
            var text = await _guides.NowNextAsync(link, DateTimeOffset.Now, _loadCts.Token);
            if (request != _guideRequest) return;
            SetGuide(text);
        }
        catch (OperationCanceledException) when (_loadCts.IsCancellationRequested)
        {
        }
        catch
        {
            if (_guideIndex is { IsFaulted: true } or { IsCanceled: true }) _guideIndex = null;
            if (request == _guideRequest) SetGuide(null);
        }
    }

    private void SetGuide(string? text)
    {
        GuideText.Text = text ?? "";
        GuideText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ChannelList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) PlaySelected();
    }

    private void ChannelList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) == null) return;
        PlaySelected();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) => HandleFullscreenKey(e);

    private void VideoOverlay_KeyDown(object sender, KeyEventArgs e) => HandleFullscreenKey(e);

    private void VideoOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is IInputElement element) element.Focus();
        if (e.ClickCount != 2) return;
        ToggleFullscreen();
        e.Handled = true;
    }

    private void HandleFullscreenKey(KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _fullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
        }
    }

    private void ToggleFullscreen()
    {
        if (_fullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        if (_fullscreen) return;
        _fullscreen = true;
        _savedStyle = WindowStyle;
        _savedState = WindowState;
        _savedVideoHeight = VideoRow.Height;
        _savedBrowserHeight = BrowserRow.Height;
        TitleRow.Height = new GridLength(0);
        SplitterRow.Height = new GridLength(0);
        BrowserRow.Height = new GridLength(0);
        BrowserRow.MinHeight = 0;
        VideoRow.MinHeight = 0;
        VideoRow.Height = new GridLength(1, GridUnitType.Star);
        TitleText.Visibility = Visibility.Collapsed;
        Splitter.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Collapsed;
        Root.Margin = new Thickness(0);
        VideoBorder.BorderThickness = new Thickness(0);
        VideoBorder.CornerRadius = new CornerRadius(0);
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
    }

    private void ExitFullscreen()
    {
        if (!_fullscreen) return;
        _fullscreen = false;
        TitleRow.Height = GridLength.Auto;
        SplitterRow.Height = GridLength.Auto;
        VideoRow.MinHeight = 120;
        VideoRow.Height = _savedVideoHeight;
        BrowserRow.MinHeight = 180;
        BrowserRow.Height = _savedBrowserHeight;
        TitleText.Visibility = Visibility.Visible;
        Splitter.Visibility = Visibility.Visible;
        Browser.Visibility = Visibility.Visible;
        Root.Margin = new Thickness(16, 12, 16, 12);
        VideoBorder.BorderThickness = new Thickness(1);
        VideoBorder.CornerRadius = new CornerRadius(12);
        WindowStyle = _savedStyle;
        WindowState = _savedState;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var volume = (int)Math.Round(e.NewValue);
        if (VolumeText != null) VolumeText.Text = volume.ToString();
        if (_player != null) _player.Volume = volume;
    }

    protected override void OnClosed(EventArgs e)
    {
        _loadCts.Cancel();
        _guides.Dispose();
        try { Video.MediaPlayer = null; } catch { }
        try
        {
            PlayerIdle = _player.ShutdownAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch
        {
            PlayerIdle = false;
        }
        base.OnClosed(e);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null && node is not T)
            node = VisualTreeHelper.GetParent(node);
        return node as T;
    }
}
