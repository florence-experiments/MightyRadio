using System.Windows;

namespace MightyRadio.Services;

public sealed record ThemeOption(string Id, string Name, string Description)
{
    public override string ToString() => Name;
}

/// <summary>Swaps the colour palette at runtime. Controls.xaml (styles) stays; only Themes/&lt;Id&gt;.xaml changes.</summary>
public static class ThemeManager
{
    public static readonly ThemeOption[] Themes =
    {
        new("Brass", "Brass & Amber", "Gunmetal and brass with an amber readout"),
        new("Phosphor", "Phosphor", "Dark console with a green phosphor readout"),
        new("Dark", "Dark", "The original dark theme with orange accents"),
    };

    public static string Current { get; private set; } = "Dark";

    public static string Normalise(string? id) =>
        Themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))?.Id ?? "Brass";

    public static void Apply(string? id)
    {
        id = Normalise(id);
        var app = Application.Current;
        if (app == null) return;
        var dicts = app.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{id}.xaml", UriKind.Absolute) };
        // Slot 0 is the palette (see App.xaml); styles in slot 1 use DynamicResource so they follow the swap.
        if (dicts.Count > 0) dicts[0] = palette;
        else dicts.Insert(0, palette);
        Current = id;
    }
}
