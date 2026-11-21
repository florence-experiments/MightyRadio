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
    private ChannelCatalog? _catalog;
    private bool _suppressFilter;

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
        var list = _catalog.Filter(NameBox.Text, country, category);
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
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyFilter();
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

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var volume = (int)Math.Round(e.NewValue);
        if (VolumeText != null) VolumeText.Text = volume.ToString();
        if (_player != null) _player.Volume = volume;
    }

    protected override void OnClosed(EventArgs e)
    {
        _loadCts.Cancel();
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
