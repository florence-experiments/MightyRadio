using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using MightyRadio.Models;

namespace MightyRadio.Services;

public enum ScheduleRepeat { Once, Daily, Weekdays }

/// <summary>One scheduled recording, saved in schedules.json. Times are local wall-clock times.</summary>
public sealed class ScheduleEntry : INotifyPropertyChanged
{
    private bool _enabled = true;
    private bool _isRunning;

    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("station")] public Station Station { get; set; } = new();
    /// <summary>First (or only) start, local time.</summary>
    [JsonPropertyName("start")] public DateTime Start { get; set; }
    [JsonPropertyName("minutes")] public int DurationMinutes { get; set; } = 60;
    [JsonPropertyName("repeat")] [JsonConverter(typeof(JsonStringEnumConverter))] public ScheduleRepeat Repeat { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get => _enabled; set { _enabled = value; Changed(nameof(Enabled)); Changed(nameof(Summary)); } }
    /// <summary>Start time of the last occurrence that was started (prevents double runs).</summary>
    [JsonPropertyName("lastRunStart")] public DateTime? LastRunStart { get; set; }

    [JsonIgnore] public bool IsRunning { get => _isRunning; set { _isRunning = value; Changed(nameof(IsRunning)); Changed(nameof(Summary)); } }
    [JsonIgnore] public TimeSpan Duration => TimeSpan.FromMinutes(Math.Clamp(DurationMinutes, 1, 24 * 60));

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var rep = Repeat switch
            {
                ScheduleRepeat.Daily => $"Daily at {Start:HH:mm}",
                ScheduleRepeat.Weekdays => $"Weekdays (Mon-Fri) at {Start:HH:mm}",
                _ => $"{Start:ddd d MMM yyyy} at {Start:HH:mm}",
            };
            var state = IsRunning ? "  ·  RECORDING NOW" : !Enabled ? "  ·  paused" : "";
            var next = NextOccurrence(DateTime.Now);
            var nextText = IsRunning || !Enabled ? "" : next == null ? "  ·  finished" : $"  ·  next {Scheduler.Friendly(next.Value)}";
            return $"{rep} for {DurationMinutes} min{state}{nextText}";
        }
    }

    public override string ToString() => $"{StationName}, {Summary}";

    [JsonIgnore] public string StationName => Station?.DisplayName ?? "(station)";

    /// <summary>
    /// The next occurrence whose recording window has not ended yet (so an app started mid-window still
    /// records the rest). Null when there are no more.
    /// </summary>
    public DateTime? NextOccurrence(DateTime now)
    {
        if (Repeat == ScheduleRepeat.Once)
        {
            if (LastRunStart != null) return null;
            return Start + Duration > now ? Start : null;
        }
        var tod = Start.TimeOfDay;
        var day = (now.Date - TimeSpan.FromDays(1) > Start.Date) ? now.Date - TimeSpan.FromDays(1) : Start.Date;
        for (int i = 0; i < 10; i++, day = day.AddDays(1))
        {
            if (Repeat == ScheduleRepeat.Weekdays && (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)) continue;
            var occ = day + tod;
            if (occ < Start) continue;
            if (LastRunStart != null && occ <= LastRunStart.Value) continue;
            if (occ + Duration <= now) continue;
            return occ;
        }
        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Runs scheduled recordings while the app is open (including minimised to the tray). UI thread only.
/// Each run gets its own <see cref="Recorder"/>, so it never interferes with a manual recording.
/// </summary>
public sealed class Scheduler
{
    private sealed class Run
    {
        public required ScheduleEntry Entry;
        public required Recorder Recorder;
        public required DateTime End;
        public int Retries;
        public DateTime? RetryAt;
        public long Bytes;
        public readonly List<string> Files = new();
    }

    private readonly Func<Recorder> _newRecorder;
    private readonly DispatcherTimer _tick;
    private readonly Dictionary<Guid, Run> _runs = new();
    private bool _readFailed;

    public ObservableCollection<ScheduleEntry> Entries { get; } = new();

    /// <summary>Status text for the UI (started, finished, failed, blocked).</summary>
    public event Action<string, bool>? Message;
    /// <summary>Raised whenever the "next recording" text may have changed.</summary>
    public event Action? Changed;

    public Scheduler(Dispatcher ui, Func<Recorder> newRecorder)
    {
        _newRecorder = newRecorder;
        var (list, status) = Storage.TryLoad(Storage.SchedulesPath, () => new List<ScheduleEntry>());
        _readFailed = status == Storage.LoadStatus.Failed;
        foreach (var e in list.Where(e => e?.Station != null && StreamUrl.IsPlayable(e.Station.StreamUrl)))
            Entries.Add(e);

        _tick = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromSeconds(10) };
        _tick.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        _tick.Start();
        Tick();
    }

    public bool HasPending => Entries.Any(e => e.Enabled && (e.IsRunning || e.NextOccurrence(DateTime.Now) != null));
    public bool IsRecording => _runs.Count > 0;

    public void Add(ScheduleEntry e)
    {
        Entries.Add(e);
        Save(userEdited: true);
        Tick();
    }

    public async Task RemoveAsync(ScheduleEntry e)
    {
        if (_runs.TryGetValue(e.Id, out var run)) await FinishAsync(run, "cancelled").ConfigureAwait(true);
        Entries.Remove(e);
        Save(userEdited: true);
        Changed?.Invoke();
    }

    public async Task SetEnabledAsync(ScheduleEntry e, bool enabled)
    {
        e.Enabled = enabled;
        if (!enabled && _runs.TryGetValue(e.Id, out var run)) await FinishAsync(run, "paused").ConfigureAwait(true);
        Save(userEdited: true);
        Tick();
    }

    private void Save(bool userEdited)
    {
        if (_readFailed && !userEdited) return;
        if (Storage.Save(Storage.SchedulesPath, Entries.ToList())) _readFailed = false;
    }

    public (ScheduleEntry Entry, DateTime When)? NextUp()
    {
        var now = DateTime.Now;
        (ScheduleEntry, DateTime)? best = null;
        foreach (var e in Entries.Where(x => x.Enabled && !x.IsRunning))
        {
            var n = e.NextOccurrence(now);
            if (n != null && (best == null || n.Value < best.Value.Item2)) best = (e, n.Value);
        }
        return best;
    }

    public string StatusText()
    {
        var running = _runs.Values.FirstOrDefault();
        if (running != null)
            return $"● Scheduled recording: {running.Entry.StationName} until {running.End:HH:mm}" + (_runs.Count > 1 ? $" (+{_runs.Count - 1} more)" : "");
        var next = NextUp();
        return next == null ? "No recordings scheduled" : $"Next recording: {next.Value.Entry.StationName}, {Friendly(next.Value.When)}";
    }

    public static string Friendly(DateTime t)
    {
        if (t.Date == DateTime.Today) return $"today {t:HH:mm}";
        if (t.Date == DateTime.Today.AddDays(1)) return $"tomorrow {t:HH:mm}";
        return t.ToString("ddd d MMM HH:mm");
    }

    private void Tick()
    {
        var now = DateTime.Now;

        foreach (var run in _runs.Values.ToList())
        {
            if (now >= run.End) { _ = FinishAsync(run, "finished"); continue; }
            if (run.RetryAt != null && now >= run.RetryAt && !run.Recorder.IsRecording)
            {
                run.RetryAt = null;
                _ = StartRecorderAsync(run);
            }
        }

        foreach (var e in Entries.Where(x => x.Enabled && !_runs.ContainsKey(x.Id)).ToList())
        {
            var occ = e.NextOccurrence(now);
            if (occ == null || now < occ.Value) continue;

            if (!RecordingConsent.IsAccepted)
            {
                // Consent is asked when a schedule is added; if the consent file was removed, don't record.
                e.LastRunStart = occ;
                Save(userEdited: false);
                Message?.Invoke($"Scheduled recording of {e.StationName} skipped: the recording notice hasn't been accepted (Settings > View recording notice).", true);
                continue;
            }

            e.LastRunStart = occ;
            Save(userEdited: false);
            var run = new Run { Entry = e, Recorder = _newRecorder(), End = occ.Value + e.Duration };
            run.Recorder.Failed += msg => OnRunFailed(run, msg);
            _runs[e.Id] = run;
            e.IsRunning = true;
            _ = StartRecorderAsync(run);
            Message?.Invoke($"Scheduled recording started: {e.StationName} until {run.End:HH:mm}.", false);
        }

        foreach (var e in Entries) e.Changed(nameof(ScheduleEntry.Summary));
        Changed?.Invoke();
    }

    private async Task StartRecorderAsync(Run run)
    {
        try
        {
            var path = await run.Recorder.StartAsync(run.Entry.Station).ConfigureAwait(true);
            if (path != null && !run.Files.Contains(path)) run.Files.Add(path);
        }
        catch (Exception ex)
        {
            Storage.Log(ex, "Scheduled recording start");
            OnRunFailed(run, "the station couldn't be recorded.");
        }
    }

    private async void OnRunFailed(Run run, string message)
    {
        try
        {
            if (!_runs.ContainsKey(run.Entry.Id)) return;
            var (path, bytes) = await run.Recorder.StopAsync().ConfigureAwait(true);
            run.Bytes += bytes;
            if (path != null && bytes > 0 && !run.Files.Contains(path)) run.Files.Add(path);
            if (!_runs.ContainsKey(run.Entry.Id)) return;
            // Retry a few times while the window is still open (Wi-Fi blips, station restarts).
            if (run.Retries < 5 && DateTime.Now + TimeSpan.FromMinutes(1) < run.End)
            {
                run.Retries++;
                run.RetryAt = DateTime.Now + TimeSpan.FromSeconds(30);
                Message?.Invoke($"Scheduled recording of {run.Entry.StationName} interrupted; retrying in 30 s ({run.Retries}/5).", true);
                return;
            }
            Message?.Invoke($"Scheduled recording of {run.Entry.StationName}: {message.Replace("Recording stopped: ", "")}", true);
            await FinishAsync(run, "failed").ConfigureAwait(true);
        }
        catch (Exception ex) { Storage.Log(ex, "Scheduled recording failure handling"); }
    }

    private async Task FinishAsync(Run run, string why)
    {
        if (!_runs.Remove(run.Entry.Id)) return;
        var (path, bytes) = await run.Recorder.StopAsync().ConfigureAwait(true);
        run.Bytes += bytes;
        if (path != null && bytes > 0 && !run.Files.Contains(path)) run.Files.Add(path);
        run.Entry.IsRunning = false;
        if (run.Entry.Repeat == ScheduleRepeat.Once && why == "finished") run.Entry.Enabled = false;
        Save(userEdited: false);
        if (why is "finished" or "failed")
        {
            if (run.Bytes > 0)
                Message?.Invoke($"Scheduled recording of {run.Entry.StationName} saved ({run.Bytes / 1024 / 1024.0:0.0} MB) in {Storage.RecordingsFolder}.", false);
            else if (why == "finished")
                Message?.Invoke($"Scheduled recording of {run.Entry.StationName} captured no audio.", true);
        }
        Changed?.Invoke();
    }

    /// <summary>Stop every running scheduled recording (app exit).</summary>
    public async Task ShutdownAsync()
    {
        _tick.Stop();
        var tasks = _runs.Values.ToList().Select(r => FinishAsync(r, "shutdown"));
        await Task.WhenAll(tasks).ConfigureAwait(true);
    }
}
