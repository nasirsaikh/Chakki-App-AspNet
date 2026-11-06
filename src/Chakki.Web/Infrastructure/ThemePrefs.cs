namespace Chakki.Web.Infrastructure;

/// <summary>
/// How the app looks on this device: light / dark, accent colour, typeface, text size, sidebar and header style, spacing.
/// Saved by the "Customize" panel in a plain cookie (no personal data) so the server renders the right look on the
/// first paint. Every value is checked against a fixed list, so the cookie can never inject CSS.
/// </summary>
public sealed record ThemePrefs(string Mode, string Accent, string Font, int Size, string Sidebar, string Header, string Density)
{
    public const string Cookie = "chakki-theme";

    public static readonly ThemePrefs Default = new("system", "blue", "source", 14, "dark", "light", "compact");

    /// <summary>AdminLTE's palette plus the chakki's own wheat colour. Every one carries white text with good contrast.</summary>
    public static readonly (string Key, string Label, string Hex, string Rgb)[] Accents =
    [
        ("blue", "Blue", "#0d6efd", "13, 110, 253"),
        ("indigo", "Indigo", "#6610f2", "102, 16, 242"),
        ("purple", "Purple", "#6f42c1", "111, 66, 193"),
        ("green", "Green", "#198754", "25, 135, 84"),
        ("teal", "Teal", "#0f766e", "15, 118, 110"),
        ("wheat", "Amber", "#b45309", "180, 83, 9"),
        ("red", "Red", "#c82333", "200, 35, 51"),
        ("dark", "Charcoal", "#343a40", "52, 58, 64"),
    ];

    public static readonly (string Key, string Label, string Family)[] Fonts =
    [
        ("source", "Source Sans 3", "\"Source Sans 3 Variable\""),
        ("inter", "Inter", "\"Inter Variable\""),
        ("roboto", "Roboto", "\"Roboto Variable\""),
        ("opensans", "Open Sans", "\"Open Sans Variable\""),
        ("nunito", "Nunito Sans", "\"Nunito Sans Variable\""),
        ("system", "Device font", "system-ui"),
    ];

    public static readonly (int Px, string Label)[] Sizes = [(13, "Small"), (14, "Normal"), (16, "Large"), (18, "Extra large")];

    public static readonly string[] Modes = ["light", "dark", "system"];
    public static readonly string[] Sidebars = ["dark", "light"];
    public static readonly string[] Headers = ["light", "dark", "primary"];
    public static readonly string[] Densities = ["compact", "comfortable"];

    public (string Key, string Label, string Hex, string Rgb) AccentColor => Accents.First(a => a.Key == Accent);
    public string FontFamily => Fonts.First(f => f.Key == Font).Family;

    /// <summary>Inline custom properties for &lt;html style&gt;; values come only from the fixed lists above.</summary>
    public string CssVariables =>
        $"--ca-primary: {AccentColor.Hex}; --ca-primary-rgb: {AccentColor.Rgb}; --ca-font-size: {Size}px; " +
        $"--ca-font: {FontFamily}, \"Noto Sans Devanagari\", \"Nirmala UI\", \"Mangal\", system-ui, -apple-system, \"Segoe UI\", Roboto, Arial, sans-serif;";

    /// <summary>Reads "mode=dark&amp;accent=green&amp;…"; anything unknown falls back to the default.</summary>
    public static ThemePrefs From(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(Cookie, out var raw) || string.IsNullOrEmpty(raw)) return Default;
        var map = raw.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.Last()[1]));
        string Pick(string key, IEnumerable<string> allowed, string fallback) =>
            map.TryGetValue(key, out var v) && allowed.Contains(v) ? v : fallback;
        var size = map.TryGetValue("size", out var s) && int.TryParse(s, out var px) && Sizes.Any(z => z.Px == px) ? px : Default.Size;
        return new ThemePrefs(
            Pick("mode", Modes, Default.Mode),
            Pick("accent", Accents.Select(a => a.Key), Default.Accent),
            Pick("font", Fonts.Select(f => f.Key), Default.Font),
            size,
            Pick("sidebar", Sidebars, Default.Sidebar),
            Pick("header", Headers, Default.Header),
            Pick("density", Densities, Default.Density));
    }
}
