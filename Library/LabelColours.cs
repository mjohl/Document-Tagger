using System.Globalization;
using System.Text;

/// <summary>
/// Colours for categories and tags. A fixed palette keeps every colour readable on the dark theme; users pick from it on the
/// Colours page. Categories start with sensible defaults (bills amber, legal red, medical green, ...) that users can change
/// or switch off; tags have no colour until one is chosen.
/// </summary>
static class LabelColours
{
    public const string None = "none";

    public sealed record Swatch(string Name, string Title, int R, int G, int B, string Text);

    public static readonly IReadOnlyList<Swatch> Palette =
    [
        new("red", "Red", 239, 68, 68, "#fecaca"),
        new("orange", "Orange", 249, 115, 22, "#fed7aa"),
        new("amber", "Amber", 245, 158, 11, "#fde68a"),
        new("yellow", "Yellow", 234, 179, 8, "#fef08a"),
        new("lime", "Lime", 132, 204, 22, "#d9f99d"),
        new("green", "Green", 34, 197, 94, "#bbf7d0"),
        new("teal", "Teal", 20, 184, 166, "#99f6e4"),
        new("cyan", "Cyan", 6, 182, 212, "#a5f3fc"),
        new("blue", "Blue", 59, 130, 246, "#bfdbfe"),
        new("indigo", "Indigo", 99, 102, 241, "#c7d2fe"),
        new("violet", "Violet", 139, 92, 246, "#ddd6fe"),
        new("pink", "Pink", 236, 72, 153, "#fbcfe8"),
        new("slate", "Grey", 148, 163, 184, "#e2e8f0"),
    ];

    private static readonly Dictionary<string, string> CategoryDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bills"] = "amber",
        ["Utilities"] = "yellow",
        ["Legal"] = "red",
        ["Medical"] = "green",
        ["Tax"] = "violet",
        ["Banking"] = "blue",
        ["Insurance"] = "cyan",
        ["Government"] = "indigo",
        ["Appointments"] = "teal",
        ["Property"] = "lime",
        ["Receipts"] = "orange",
        ["Subscriptions"] = "pink",
        ["Travel"] = "teal",
        ["Education"] = "indigo",
        ["Employment"] = "blue",
        ["Correspondence"] = "slate",
    };

    public static bool IsValid(string? colour) => colour == None || Palette.Any(swatch => swatch.Name == colour);

    /// <summary>The colour of every coloured label: category defaults, with the user's choices applied on top.</summary>
    public static IReadOnlyDictionary<string, string> Effective(IReadOnlyDictionary<string, string> chosen)
    {
        var colours = new Dictionary<string, string>(CategoryDefaults, StringComparer.OrdinalIgnoreCase);
        foreach (var (label, colour) in chosen)
        {
            if (colour == None) colours.Remove(label);
            else colours[label] = colour;
        }
        return colours;
    }

    public static string? For(string? label, IReadOnlyDictionary<string, string> colours) =>
        label is not null && colours.TryGetValue(label.Trim(), out var colour) ? colour : null;

    /// <summary>A document's colour: its first coloured tag, which is the most specific, otherwise its category's colour.</summary>
    public static string? ForDocument(IEnumerable<string> tags, string? category, IReadOnlyDictionary<string, string> colours) =>
        tags.Select(tag => For(tag, colours)).FirstOrDefault(colour => colour is not null) ?? For(category, colours);

    /// <summary>The CSS class for a chip in this colour, or an empty string for an uncoloured label.</summary>
    public static string ChipClass(string? colour) => colour is null ? "" : " c-" + colour;
    public static string AccentClass(string? colour) => colour is null ? "" : " accent-" + colour;

    /// <summary>
    /// One rule set per palette colour: <c>.c-*</c> tints a chip, <c>.accent-*</c> sets the <c>--accent</c> colour used for row
    /// edges and calendar entries, and <c>.swatch-*</c> fills a colour-picker swatch.
    /// </summary>
    public static string Css { get; } = BuildCss();

    private static string BuildCss()
    {
        var css = new StringBuilder();
        foreach (var swatch in Palette)
        {
            var rgb = string.Create(CultureInfo.InvariantCulture, $"{swatch.R},{swatch.G},{swatch.B}");
            css.Append(CultureInfo.InvariantCulture, $".c-{swatch.Name}{{background:rgba({rgb},.17);border-color:rgba({rgb},.5);color:{swatch.Text}}}");
            css.Append(CultureInfo.InvariantCulture, $".accent-{swatch.Name}{{--accent:rgb({rgb});--accent-soft:rgba({rgb},.16);--accent-text:{swatch.Text}}}");
            css.Append(CultureInfo.InvariantCulture, $".swatch-{swatch.Name}{{--swatch:rgb({rgb})}}");
        }
        return css.ToString();
    }
}
