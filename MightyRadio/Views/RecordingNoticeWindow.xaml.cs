using System.Windows;
using MightyRadio.Services;

namespace MightyRadio.Views;

/// <summary>Legal notice shown before the first recording; also viewable from Settings.</summary>
public partial class RecordingNoticeWindow : Window
{
    private readonly bool _viewOnly;

    private RecordingNoticeWindow(bool viewOnly)
    {
        InitializeComponent();
        _viewOnly = viewOnly;
        NoticeText.Text = RecordingConsent.NoticeText;
        if (viewOnly)
        {
            Title = "Recording notice - Mighty Radio";
            AcceptBox.IsChecked = true;
            AcceptBox.IsEnabled = false;
            AcceptBox.Content = "You accepted this notice" + AcceptedSuffix();
            ContinueButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Close";
        }
        Loaded += (_, _) => (viewOnly ? (UIElement)CancelButton : AcceptBox).Focus();
    }

    private static string AcceptedSuffix()
    {
        try
        {
            var r = Storage.Load<RecordingConsent.Record?>(Storage.ConsentPath, () => null);
            return r == null ? "" : $" on {r.AcceptedAt.LocalDateTime:d MMM yyyy, HH:mm}";
        }
        catch { return ""; }
    }

    private void AcceptBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_viewOnly) ContinueButton.IsEnabled = AcceptBox.IsChecked == true;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (AcceptBox.IsChecked != true) return;
        RecordingConsent.Save();
        DialogResult = true;
    }

    /// <summary>True if recording may go ahead (already accepted, or accepted now).</summary>
    public static bool EnsureConsent(Window? owner)
    {
        if (RecordingConsent.IsAccepted) return true;
        var w = new RecordingNoticeWindow(viewOnly: false);
        if (owner is { IsVisible: true }) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w.ShowDialog() == true && RecordingConsent.IsAccepted;
    }

    /// <summary>Show the notice again (Settings). Offers acceptance if it hasn't been accepted yet.</summary>
    public static void ShowNotice(Window? owner)
    {
        if (!RecordingConsent.IsAccepted) { EnsureConsent(owner); return; }
        var w = new RecordingNoticeWindow(viewOnly: true);
        if (owner is { IsVisible: true }) w.Owner = owner;
        w.ShowDialog();
    }
}
