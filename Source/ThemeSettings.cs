using UnityEngine;
using Verse;

namespace FlatBlack;

internal enum ThemeFontPreset { Vanilla, OxaniumExo2 }

internal record struct ThemeOptions
{
    public ThemeFontPreset FontPreset;
    public float WindowOpacity;
    public float PanelOpacity;
    public bool Backgrounds;
    public bool Icons;
    public bool Highlights;
    public bool CustomAccents;
    public Color PrimaryAccent;
    public Color SecondaryAccent;

    internal readonly bool IsEnabled(ThemeComponent component) => component switch
    {
        ThemeComponent.Backgrounds => Backgrounds,
        ThemeComponent.Icons => Icons,
        ThemeComponent.Highlights => Highlights,
        _ => throw new ArgumentOutOfRangeException(nameof(component))
    };

    internal readonly float OpacityScale(OpacityChannel channel, ThemeDef theme) => channel switch
    {
        OpacityChannel.None => 1f,
        OpacityChannel.Window => WindowOpacity / theme.DefaultOpacity(channel),
        OpacityChannel.Panel => PanelOpacity / theme.DefaultOpacity(channel),
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    internal readonly void Validate()
    {
        if (!Enum.IsDefined(FontPreset))
            throw new ArgumentOutOfRangeException(nameof(FontPreset), FontPreset, "Unknown font preset.");
        ValidateUnit(WindowOpacity, nameof(WindowOpacity));
        ValidateUnit(PanelOpacity, nameof(PanelOpacity));
        ValidateAccent(PrimaryAccent, nameof(PrimaryAccent));
        ValidateAccent(SecondaryAccent, nameof(SecondaryAccent));
    }

    internal static void ValidateUnit(float value, string name)
    {
        if (!float.IsFinite(value) || value is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(name, value, "The value must be finite and between 0 and 1.");
    }

    private static void ValidateAccent(Color value, string name)
    {
        for (int component = 0; component < 4; component++)
            ValidateUnit(value[component], name);
        if (value.a != 1f)
            throw new ArgumentException("Accent colours must be opaque.", name);
    }

    internal readonly Color Recolor(Color source)
    {
        if (!CustomAccents)
            return source;

        Color.RGBToHSV(source, out float hue, out float saturation, out float value);
        if (saturation < 0.1f || value == 0f)
            return source;

        Color target;
        if (hue <= 1f / 12f || hue >= 11f / 12f)
            target = PrimaryAccent;
        else if (hue is >= 2f / 3f and <= 11f / 12f)
            target = SecondaryAccent;
        else
            return source;

        Color.RGBToHSV(target, out float targetHue, out float targetSaturation, out float targetValue);
        Color result = Color.HSVToRGB(targetHue, saturation * targetSaturation, value * targetValue);
        result.a = source.a;
        return result;
    }
}

public sealed class ThemeSettings : ModSettings
{
    private static ThemeOptions? defaults;
    internal ThemeOptions options = Defaults;
    internal static ThemeOptions Defaults => defaults ?? throw new InvalidOperationException("Theme defaults have not been initialized.");

    internal static void ConfigureDefaults(ThemeDef theme)
    {
        ThemeOptions values = theme.DefaultOptions();
        values.Validate();
        if (values.WindowOpacity == 0f || values.PanelOpacity == 0f)
            throw new InvalidOperationException("Default theme opacities must be greater than zero.");
        defaults = values;
    }

    internal void ResetDefaults() => options = Defaults;

    public override void ExposeData()
    {
        ThemeOptions baseline = Defaults;
        Scribe_Values.Look(ref options.FontPreset, "fontPreset", baseline.FontPreset, forceSave: true);
        Scribe_Values.Look(ref options.WindowOpacity, "windowOpacity", baseline.WindowOpacity);
        Scribe_Values.Look(ref options.PanelOpacity, "panelOpacity", baseline.PanelOpacity);
        Scribe_Values.Look(ref options.Backgrounds, "backgrounds", baseline.Backgrounds);
        Scribe_Values.Look(ref options.Icons, "icons", baseline.Icons);
        Scribe_Values.Look(ref options.Highlights, "highlights", baseline.Highlights);
        Scribe_Values.Look(ref options.CustomAccents, "customAccents", baseline.CustomAccents);
        Scribe_Values.Look(ref options.PrimaryAccent, "primaryAccent", baseline.PrimaryAccent);
        Scribe_Values.Look(ref options.SecondaryAccent, "secondaryAccent", baseline.SecondaryAccent);
        options.Validate();
    }
}
