using HarmonyLib;
using UnityEngine;
using UnityStubs.Gui;
using Verse;

namespace FlatBlack;

/// <summary>Switches standard text fonts without replacing styles or later font overrides.</summary>
internal sealed class ThemeFonts
{
    private readonly record struct StyleBinding(GUIStyle[] Styles, int Index, GUIStyle Style, Font Original);
    private readonly record struct LineMetrics(float Height, float Spacing);

    private static readonly GameFont[] Sizes = Enum.GetValues<GameFont>();
    private static readonly GUIContent SingleLine = new("W");
    private static readonly GUIContent DoubleLine = new("W\nW");
    private const float MeasurementWidth = 999f;
    private readonly Func<Font[]> loadFonts;
    private readonly float[] lineHeights = AccessTools.StaticFieldRefAccess<float[]>(typeof(Text), "lineHeights");
    private readonly float[] lineSpacing = AccessTools.StaticFieldRefAccess<float[]>(typeof(Text), "spaceBetweenLines");
    private readonly StyleBinding[] bindings = new[] { Text.fontStyles, Text.textFieldStyles, Text.textAreaStyles, Text.textAreaReadOnlyStyles }
        .SelectMany(styles => Sizes.Select(size => new StyleBinding(styles, (int)size, styles[(int)size], styles[(int)size].font)))
        .ToArray();
    private readonly LineMetrics?[] appliedMetrics = new LineMetrics?[Sizes.Length];
    private readonly LineMetrics[] originalMetrics;
    private Font[]? fonts;
    private ThemeFontPreset preset;

    internal ThemeFonts(Func<Font[]> loadFonts)
    {
        this.loadFonts = loadFonts;
        originalMetrics = Sizes.Select(size => new LineMetrics(lineHeights[(int)size], lineSpacing[(int)size])).ToArray();
    }

    internal static Font[] Load(string rootDirectory)
    {
        IGuiBackend backend = GuiBackend.Current ?? throw new InvalidOperationException("The font renderer is not available.");
        string directory = Path.Combine(rootDirectory, "1.7", "Fonts");
        byte[] body = File.ReadAllBytes(Path.Combine(directory, "Exo2", "Exo2-Regular.ttf"));
        byte[] heading = File.ReadAllBytes(Path.Combine(directory, "Oxanium", "Oxanium-Medium.ttf"));
        return Sizes.Select(size =>
        {
            bool isHeading = size == GameFont.Medium;
            string name = isHeading ? "FlatBlack/Oxanium-Medium" : "FlatBlack/Exo2-Regular";
            int index = backend.RegisterFont(name, isHeading ? heading : body, Text.fontStyles[(int)size].font.fontSize, 0);
            if (index < 0)
                throw new InvalidOperationException($"Could not register {name} for {size} text.");
            return new Font { name = name, GameFontIndex = index };
        }).ToArray();
    }

    internal void Apply(ThemeFontPreset value)
    {
        if (preset == value)
            return;
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(value));

        Font[] replacements = fonts ?? loadFonts();
        if (replacements.Length != Sizes.Length)
            throw new InvalidOperationException("A font preset must provide every game text size.");
        fonts = replacements;

        Span<LineMetrics?> nextMetrics = stackalloc LineMetrics?[Sizes.Length];
        nextMetrics.Clear();
        foreach (GameFont size in Sizes)
        {
            int index = (int)size;
            StyleBinding label = bindings[index];
            var currentMetrics = new LineMetrics(lineHeights[index], lineSpacing[index]);
            if (!CanUpdate(label, replacements[index]) || currentMetrics != (appliedMetrics[index] ?? originalMetrics[index]))
                continue;

            LineMetrics next = originalMetrics[index];
            if (value != ThemeFontPreset.Vanilla)
            {
                // Measure before changing shared styles so a renderer failure leaves the active preset intact.
                var measurementStyle = new GUIStyle(label.Style) { font = replacements[index] };
                float height = measurementStyle.CalcHeight(SingleLine, MeasurementWidth);
                next = new LineMetrics(height, measurementStyle.CalcHeight(DoubleLine, MeasurementWidth) - height * 2f);
            }
            nextMetrics[index] = next;
        }

        foreach (StyleBinding binding in bindings)
        {
            if (CanUpdate(binding, replacements[binding.Index]))
                binding.Style.font = value == ThemeFontPreset.Vanilla ? binding.Original : replacements[binding.Index];
        }
        foreach (GameFont size in Sizes)
        {
            int index = (int)size;
            StyleBinding label = bindings[index];
            Font expected = value == ThemeFontPreset.Vanilla ? label.Original : replacements[index];
            if (nextMetrics[index] is not LineMetrics next || !ReferenceEquals(Text.fontStyles[index], label.Style)
                || !ReferenceEquals(label.Style.font, expected))
                continue;
            lineHeights[index] = next.Height;
            lineSpacing[index] = next.Spacing;
            appliedMetrics[index] = value == ThemeFontPreset.Vanilla ? null : next;
        }
        preset = value;
    }

    private static bool CanUpdate(StyleBinding binding, Font replacement) =>
        ReferenceEquals(binding.Styles[binding.Index], binding.Style)
        && (ReferenceEquals(binding.Style.font, binding.Original) || ReferenceEquals(binding.Style.font, replacement));
}
