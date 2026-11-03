using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MightyRadio.Services;

public sealed record ThemeOption(string Id, string Name, string Description)
{
    public override string ToString() => Name;
}

/// <summary>
/// Swaps the colour palette at runtime. Controls.xaml (styles) stays; only Themes/&lt;Id&gt;.xaml in
/// MergedDictionaries[0] changes.
///
/// WPF resolves resources inside templates only when a control is built, so a bad palette or style would
/// otherwise fail in the middle of a layout pass and leave a half-drawn window. Every switch is therefore
/// checked first (same keys and value types as the Dark palette) and then exercised on an off-screen set
/// of controls; if either step fails the previous palette is put back and the caller gets the reason.
/// Run tools/check-xaml-resources.py to catch the same problems before a release.
/// </summary>
public static class ThemeManager
{
    public const string FallbackId = "Dark";

    public static readonly ThemeOption[] Themes =
    {
        new("Brass", "Brass & Amber", "Gunmetal and brass with an amber readout"),
        new("Phosphor", "Phosphor", "Dark console with a green phosphor readout"),
        new("Dark", "Dark", "The original dark theme with orange accents"),
    };

    /// <summary>The palette in App.xaml's slot 0 when the app starts.</summary>
    public static string Current { get; private set; } = "Brass";

    public static string Normalise(string? id) =>
        Themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))?.Id ?? "Brass";

    private static ResourceDictionary LoadPalette(string id) =>
        new() { Source = new Uri($"pack://application:,,,/Themes/{id}.xaml", UriKind.Absolute) };

    /// <summary>
    /// Switches to <paramref name="id"/>. Returns null on success, or a short reason (already logged) when the
    /// theme was rejected; in that case the previous palette is still active.
    /// </summary>
    public static string? TryApply(string? id)
    {
        id = Normalise(id);
        var app = Application.Current;
        if (app == null) return "no application";
        var dicts = app.Resources.MergedDictionaries;
        var previous = dicts.Count > 0 ? dicts[0] : null;
        try
        {
            var palette = LoadPalette(id);
            ValidatePalette(id, palette);
            if (dicts.Count > 0) dicts[0] = palette; else dicts.Insert(0, palette);
            Probe();
            Current = id;
            return null;
        }
        catch (Exception ex)
        {
            Storage.Log(ex, $"Theme '{id}' failed its check");
            try
            {
                if (previous != null && dicts.Count > 0 && !ReferenceEquals(dicts[0], previous)) dicts[0] = previous;
            }
            catch (Exception restoreEx) { Storage.Log(restoreEx, "Restoring the previous theme"); }
            return ex.Message;
        }
    }

    /// <summary>
    /// Startup: use the saved theme, or Dark if it fails. Returns a message for the user when the saved
    /// theme was replaced, otherwise null. If even Dark fails its check, the app carries on with whatever
    /// is loaded and the problem is in error.log (the check itself might be what is wrong).
    /// </summary>
    public static string? ApplyAtStartup(string? id)
    {
        id = Normalise(id);
        var reason = TryApply(id);
        if (reason == null) return null;
        if (id != FallbackId && TryApply(FallbackId) == null)
            return $"The {Name(id)} theme couldn't be loaded, so Mighty Radio switched to the Dark theme. ({reason})";
        return null; // the palette from App.xaml is still in place and Current still names it
    }

    public static string Name(string id) => Themes.FirstOrDefault(t => t.Id == id)?.Name ?? id;

    /// <summary>Same keys, same kinds of value as the Dark palette (the reference).</summary>
    private static void ValidatePalette(string id, ResourceDictionary palette)
    {
        var reference = id == FallbackId ? palette : LoadPalette(FallbackId);
        var problems = new List<string>();
        foreach (var key in reference.Keys.OfType<string>())
        {
            if (!palette.Contains(key)) { problems.Add($"missing '{key}'"); continue; }
            var want = Kind(reference[key]);
            var got = Kind(palette[key]);
            if (want != got) problems.Add($"'{key}' is a {got}, expected {want}");
        }
        if (problems.Count > 0)
            throw new InvalidOperationException($"Theme '{id}': " + string.Join("; ", problems));
    }

    private static string Kind(object? value) => value switch
    {
        Brush => "Brush",
        Effect => "Effect",
        null => "null",
        _ => value.GetType().Name,
    };

    /// <summary>
    /// Builds one of each styled control off-screen and runs layout, so every template and every resource
    /// they reference is resolved now (and throws now) rather than in the middle of drawing the window.
    /// </summary>
    public static void Probe()
    {
        var app = Application.Current;
        if (app == null) return;
        Style S(string key) => (Style)app.FindResource(key);
        DataTemplate T(string key) => (DataTemplate)app.FindResource(key);

        var items = Enumerable.Range(1, 40).Select(i => "Item " + i).ToList();
        var list = new ListBox { ItemsSource = items, Height = 160, Width = 260, ItemTemplate = T("StationTemplate") };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Visible);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Visible);

        var tabs = new TabControl { Height = 120 };
        tabs.Items.Add(new TabItem { Header = "One", Content = new TextBlock { Text = "x" } });
        tabs.Items.Add(new TabItem { Header = "Two" });
        tabs.SelectedIndex = 0;

        var panel = new StackPanel();
        void Add(UIElement e) => panel.Children.Add(e);
        Add(new Border { Style = S("Panel"), Child = new TextBlock { Text = "p", Style = S("SectionHeader") } });
        Add(new Border
        {
            Style = S("Readout"),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "a", Style = S("ReadoutText") },
                    new TextBlock { Text = "b", Style = S("ReadoutSubtleText") },
                    new TextBlock { Text = "c", Style = S("ReadoutStatusText") },
                    new TextBlock { Text = "d", Style = S("FieldLabel") },
                    new TextBlock { Text = "e", Style = S("SubtleText") },
                },
            },
        });
        Add(new Button { Content = "b" });
        Add(new Button { Content = "a", Style = S("AccentButton") });
        Add(new TextBox { Text = "t" });
        Add(new ComboBox { ItemsSource = new[] { "x", "y" }, SelectedIndex = 0 });
        Add(new CheckBox { Content = "c", IsChecked = true });
        Add(new Slider { Width = 120, Maximum = 100, Value = 50 });
        Add(new ScrollBar { Orientation = Orientation.Vertical, Height = 200 });
        Add(new ScrollBar { Orientation = Orientation.Horizontal, Width = 200 });
        Add(new ScrollViewer
        {
            Width = 150, Height = 150,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            Content = new Border { Width = 400, Height = 400 },
        });
        Add(list);
        Add(new ListBox { ItemsSource = items.Take(3).ToList(), ItemTemplate = T("HistoryTemplate") });
        Add(new ListBox { ItemsSource = items.Take(3).ToList(), ItemTemplate = T("ScheduleTemplate") });
        Add(tabs);

        panel.Measure(new Size(900, 3000));
        panel.Arrange(new Rect(0, 0, 900, 3000));
        panel.UpdateLayout();
    }
}
