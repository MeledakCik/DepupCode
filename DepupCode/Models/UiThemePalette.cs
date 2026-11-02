using System.Windows.Media;

namespace DepupCode.Models;

public static class UiThemePalette
{
    private static readonly string[] Roles =
    [
        "window", "title", "activity", "sidebar", "panel", "editor", "terminal", "surface", "surfaceHover",
        "input", "border", "borderLight", "separator", "status", "popup", "menu", "selectedPanel",
        "primary", "secondary", "soft", "muted", "dim", "activityIdle", "accent", "accentHover", "accentSoft",
        "lineNumber", "surfaceAlt", "activeNav", "activeSurface", "buttonSurface"
    ];

    private static readonly Dictionary<string, IReadOnlyDictionary<string, Color>> Themes = new(
        BuildThemes().ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> CustomSyntaxThemes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CustomThemeNames = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Names => Themes.Keys.ToArray();

    public static IReadOnlyDictionary<string, Color> Get(string? name) =>
        Themes.TryGetValue(name ?? "", out var palette) ? palette : Themes["Depup Dark"];

    public static string Normalize(string? name) => Themes.ContainsKey(name ?? "") ? name! : "Depup Dark";

    public static void RegisterCustomTheme(string name, IReadOnlyDictionary<string, string> colors,
        IReadOnlyDictionary<string, string>? syntaxColors = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var palette = Themes["Depup Dark"].ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (role, value) in colors)
        {
            if (!palette.ContainsKey(role)) continue;
            try { palette[role] = (Color)ColorConverter.ConvertFromString(value)!; }
            catch { }
        }
        palette["lineNumber"] = EnsureReadableLineNumber(palette["editor"], palette["lineNumber"]);
        Themes[name] = palette;
        CustomThemeNames.Add(name);
        if (syntaxColors is not null) CustomSyntaxThemes[name] = new Dictionary<string, string>(syntaxColors, StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryGetCustomSyntaxTheme(string? name, out IReadOnlyDictionary<string, string> syntaxColors) =>
        CustomSyntaxThemes.TryGetValue(name ?? "", out syntaxColors!);

    public static void ClearCustomThemes()
    {
        foreach (var name in CustomThemeNames) Themes.Remove(name);
        CustomThemeNames.Clear();
        CustomSyntaxThemes.Clear();
    }

    private static Color EnsureReadableLineNumber(Color background, Color lineNumber)
    {
        if (ContrastRatio(background, lineNumber) >= 3.2) return lineNumber;
        var target = RelativeLuminance(background) > 0.5 ? Colors.Black : Colors.White;
        for (var step = 1; step <= 10; step++)
        {
            var amount = step / 10d;
            var adjusted = Color.FromRgb(
                (byte)Math.Round(lineNumber.R + (target.R - lineNumber.R) * amount),
                (byte)Math.Round(lineNumber.G + (target.G - lineNumber.G) * amount),
                (byte)Math.Round(lineNumber.B + (target.B - lineNumber.B) * amount));
            if (ContrastRatio(background, adjusted) >= 3.2) return adjusted;
        }
        return target;
    }

    private static double ContrastRatio(Color left, Color right)
    {
        var first = RelativeLuminance(left);
        var second = RelativeLuminance(right);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, Color>> BuildThemes() =>
        new Dictionary<string, IReadOnlyDictionary<string, Color>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Depup Dark"] = Palette(
                "#0C0A17", "#0F0B1E", "#0F0A1C", "#140C26", "#120F24", "#0D0718", "#090410", "#1C1736", "#251F47",
                "#100A20", "#2A224F", "#382F69", "#261E47", "#0B0716", "#16132B", "#141026", "#181330",
                "#F8FAFC", "#CBD5E1", "#E2E8F0", "#9490B8", "#6C6794", "#7C77A4", "#8B5CF6", "#7C3AED", "#A78BFA",
                "#A29ABF", "#1B1433", "#211A40", "#241B45", "#332660"),
            ["VS Code Dark"] = Palette(
                "#1D1D1D", "#1A1A1A", "#191919", "#252526", "#202020", "#1E1E1E", "#222222", "#2D2D30", "#383838",
                "#333333", "#454545", "#575757", "#2B2B2B", "#242424", "#262626", "#2A2D2E", "#292929",
                "#D4D4D4", "#C8C8C8", "#BDBDBD", "#9D9D9D", "#858585", "#808080", "#007ACC", "#0062A3", "#3794FF",
                "#929292", "#232323", "#303030", "#323236", "#3A3A3A"),
            ["Midnight Blue"] = Palette(
                "#0B1220", "#0D1728", "#101B2D", "#111D30", "#142035", "#0C1422", "#0D1524", "#1B2A40", "#243750",
                "#101C2D", "#263A55", "#345273", "#1A2A40", "#111C2B", "#17243A", "#1A2942", "#1D2D49",
                "#D6E4FF", "#C3D3EB", "#B1C8E6", "#8FA7C4", "#7088A8", "#7188A6", "#60A5FA", "#3B82F6", "#82AAFF",
                "#98A9C1", "#19273B", "#20334D", "#223854", "#2C4564"),
            ["Dracula"] = Palette(
                "#282A36", "#21222C", "#242530", "#292B38", "#262833", "#272934", "#23252F", "#343746", "#414458",
                "#2E303D", "#44475A", "#5B5F76", "#30323F", "#252733", "#313340", "#353847", "#383B4B",
                "#F8F8F2", "#E6E6DC", "#D9D9D0", "#B9BAC4", "#9294A1", "#8E90A0", "#BD93F9", "#A879E8", "#D0A9FF",
                "#A1A9C9", "#30323D", "#373948", "#3B3E50", "#484B60"),
            ["Nord"] = Palette(
                "#2E3440", "#292F3A", "#2B313D", "#343B49", "#303744", "#2F3541", "#282E39", "#3B4252", "#454E60",
                "#353D4B", "#4C566A", "#5D687C", "#333B49", "#2A303B", "#394252", "#404A5C", "#434D5E",
                "#ECEFF4", "#E2E7EE", "#D7DDE6", "#BBC3D0", "#929BAA", "#8D97A6", "#88C0D0", "#6FAFC1", "#A3D3DE",
                "#9AA6BC", "#363E4D", "#3C4656", "#414B5C", "#4A5569"),
            ["Light+"] = Palette(
                "#F3F3F3", "#EEEEEE", "#E9E9E9", "#F1F1F1", "#F7F7F7", "#FFFFFF", "#FCFCFC", "#EAEAEA", "#DDDDDD",
                "#FAFAFA", "#D4D4D4", "#C8C8C8", "#E5E5E5", "#E8E8E8", "#FBFBFB", "#F8F8F8", "#F0F0F0",
                "#333333", "#4B4B4B", "#595959", "#666666", "#777777", "#808080", "#007ACC", "#0062A3", "#3794FF",
                "#666666", "#EBEBEB", "#DCEEFF", "#E3F2FD", "#D8D8D8"),
            ["High Contrast"] = Palette(
                "#000000", "#070707", "#050505", "#101010", "#0B0B0B", "#010101", "#080808", "#161616", "#242424",
                "#111111", "#767676", "#A0A0A0", "#333333", "#121212", "#151515", "#202020", "#292929",
                "#FFFFFF", "#F0F0F0", "#E2E2E2", "#C8C8C8", "#AAAAAA", "#BBBBBB", "#00E5FF", "#00B8D4", "#66F0FF",
                "#FFD700", "#191919", "#252525", "#303030", "#404040")
        };

    private static IReadOnlyDictionary<string, Color> Palette(params string[] values)
    {
        if (values.Length != Roles.Length)
            throw new InvalidOperationException($"Theme palette must define {Roles.Length} colors; got {values.Length}.");

        return Roles.Select((role, index) => (role, color: (Color)ColorConverter.ConvertFromString(values[index])!))
            .ToDictionary(item => item.role, item => item.color, StringComparer.OrdinalIgnoreCase);
    }
}
