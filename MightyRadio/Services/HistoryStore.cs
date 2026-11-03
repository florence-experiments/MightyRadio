using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace MightyRadio.Services;

/// <summary>One "now playing" title heard on a station.</summary>
public sealed class HistoryEntry
{
    [JsonPropertyName("time")] public DateTime Time { get; set; }
    [JsonPropertyName("station")] public string Station { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";

    [JsonIgnore] public string TimeText => Time.Date == DateTime.Today ? Time.ToString("HH:mm") : Time.ToString("ddd d MMM HH:mm");
    public override string ToString() => $"{Title}, {Station}, {TimeText}";

    [JsonIgnore] public string Line => $"{Time:yyyy-MM-dd HH:mm}\t{Station}\t{Title}";
}

/// <summary>
/// Now-playing history, newest first, saved to %APPDATA%\MightyRadio\history.json.
/// Capped at <see cref="MaxEntries"/>; consecutive repeats are ignored; saves are debounced. UI thread only.
/// </summary>
public sealed class HistoryStore
{
    public const int MaxEntries = 1000;

    private readonly DispatcherTimer _saveTimer;
    private bool _readFailed;

    public ObservableCollection<HistoryEntry> Items { get; } = new();

    public HistoryStore(Dispatcher ui)
    {
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(3) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        var (list, status) = Storage.TryLoad(Storage.HistoryPath, () => new List<HistoryEntry>());
        _readFailed = status == Storage.LoadStatus.Failed;
        foreach (var e in list.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Title))
                              .OrderByDescending(e => e.Time).Take(MaxEntries))
            Items.Add(e);
    }

    public void Add(string? station, string? title)
    {
        title = title?.Trim();
        station = string.IsNullOrWhiteSpace(station) ? "(unknown station)" : station.Trim();
        if (string.IsNullOrWhiteSpace(title)) return;
        // Some stations put their own name in StreamTitle; still useful, but skip exact repeats.
        if (Items.Count > 0 && Items[0].Title == title && Items[0].Station == station) return;

        Items.Insert(0, new HistoryEntry { Time = DateTime.Now, Station = station, Title = title });
        while (Items.Count > MaxEntries) Items.RemoveAt(Items.Count - 1);
        _readFailed = false; // the user now has new history; the unreadable file was already backed up
        ScheduleSave();
    }

    public void Remove(IEnumerable<HistoryEntry> entries)
    {
        foreach (var e in entries.ToList()) Items.Remove(e);
        _readFailed = false;
        ScheduleSave();
    }

    public void Clear()
    {
        Items.Clear();
        _readFailed = false;
        SaveNow();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Flush()
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveNow(); }
    }

    private void SaveNow()
    {
        if (_readFailed) return;
        Storage.Save(Storage.HistoryPath, Items.ToList());
    }
}
