using System.Reflection;
using UnityEngine;
using UnityStubs.Gui;
using Verse;

namespace FlatBlack;

public sealed class FlatBlackMod : Mod
{
    private static readonly string[] ColorChannels = ["FlatBlack_Red", "FlatBlack_Green", "FlatBlack_Blue"];
    private static readonly ThemeFontPreset[] FontPresets = Enum.GetValues<ThemeFontPreset>();
    private ThemeSettings? settings;
    private ThemeFonts? fonts;
    private ThemeFontPreset? failedFontPreset;
    private ThemeOptions appliedOptions;
    private Vector2 settingsScroll;
    private readonly ThemePreview preview = new();
    private bool initialized;

    internal ThemeSettings Settings => settings ?? throw new InvalidOperationException("Flat Black settings have not been loaded.");

    public FlatBlackMod(ModContentPack content) : base(content)
    {
        LongEventHandler.ExecuteWhenFinished(Initialize);
    }

    private void Initialize()
    {
        try
        {
            ThemeDef[] themes = Content.AllDefs.OfType<ThemeDef>().ToArray();
            if (themes.Length != 1)
                throw new InvalidOperationException($"Expected one Flat Black theme definition, found {themes.Length}.");

            themes[0].Validate();
            ThemeSettings.ConfigureDefaults(themes[0]);
            settings = GetSettings<ThemeSettings>();
            fonts = new ThemeFonts(() => ThemeFonts.Load(Content.RootDir));
            IReadOnlySet<FieldInfo> fields = ThemeResources.Load(themes[0], LoadTexture, FindTextureOverride);
            ThemeResources.Apply(settings.options);
            appliedOptions = settings.options;
            int methods = ThemePatches.Install(fields, ApplyFonts);
            initialized = true;
            Log.Message($"[Flat Black] Applied {themes[0].textures.Count} texture overrides and {themes[0].colors.Count} palette colours in {methods} vanilla methods.");
        }
        catch (Exception exception)
        {
            Log.Error($"[Flat Black] Could not initialize the theme. {exception}");
        }
    }

    private Texture2D LoadTexture(string path) => ContentFinder<Texture2D>.Get(path)
        ?? throw new InvalidOperationException($"Could not load UI texture: {path}.");

    private Texture2D? FindTextureOverride(TextureOverride entry, Texture2D original)
    {
        if (Content.GetContentHolder<Texture2D>().Get(entry.texturePath) is null)
            throw new InvalidOperationException($"Missing Flat Black texture: {entry.texturePath}.");
        if (entry.vanillaPath.Length == 0)
            return null;

        Texture2D? later = LoadedModManager.RunningMods.SkipWhile(mod => mod != Content).Skip(1).Reverse()
            .Select(mod => mod.GetContentHolder<Texture2D>().Get(entry.vanillaPath)).FirstOrDefault(texture => texture is not null);
        if (later is not null)
        {
            // A readonly field can still hold an early cached vanilla asset after a later mod has replaced its path.
            return later;
        }
        return ReferenceEquals(ContentFinder<Texture2D>.Get(entry.vanillaPath, false), original) ? null : original;
    }

    public override string SettingsCategory() => "Flat Black";

    public override void WriteSettings()
    {
        if (initialized)
            ApplyOptions();
        base.WriteSettings();
    }

    internal void ApplyOptions()
    {
        if (Settings.options == appliedOptions)
            return;
        ApplyFonts();
        ThemeOptions resourceOptions = Settings.options with { FontPreset = appliedOptions.FontPreset };
        if (resourceOptions != appliedOptions)
            ThemeResources.Apply(Settings.options);
        appliedOptions = Settings.options;
    }

    private void ApplyFonts()
    {
        if (GuiBackend.Current is null || failedFontPreset == Settings.options.FontPreset)
            return;
        failedFontPreset = null;
        try
        {
            fonts!.Apply(Settings.options.FontPreset);
        }
        catch (Exception exception)
        {
            failedFontPreset = Settings.options.FontPreset;
            Log.Error($"[Flat Black] Could not apply the {Settings.options.FontPreset} font preset. {exception}");
        }
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        if (!initialized)
        {
            Widgets.Label(inRect, "FlatBlack_Unavailable".Translate());
            return;
        }

        Color previousColor = GUI.color;
        GameFont previousFont = Text.Font;
        TextAnchor previousAnchor = Text.Anchor;
        bool previousWordWrap = Text.WordWrap;
        try
        {
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            float width = inRect.width - 20f;
            float height = SettingsContents(width, draw: false);
            (Rect controls, Rect previewRect) = ThemePreview.Arrange(inRect);
            Widgets.BeginScrollView(controls, ref settingsScroll, new Rect(0, 0, width, height));
            try
            {
                SettingsContents(width, draw: true);
            }
            finally
            {
                Widgets.EndScrollView();
            }
            ApplyOptions();
            preview.Draw(previewRect);
        }
        finally
        {
            GUI.color = previousColor;
            Text.Font = previousFont;
            Text.Anchor = previousAnchor;
            Text.WordWrap = previousWordWrap;
        }
    }

    private float SettingsContents(float width, bool draw)
    {
        float y = 0f;
        string intro = "FlatBlack_SettingsIntro".Translate();
        float introHeight = Text.CalcHeight(intro, width);
        if (draw)
            Widgets.Label(new Rect(0, y, width, introHeight), intro);
        y += introHeight + 8f;
        DrawFontPreset(width, ref y, draw);
        DrawToggle(width, ref y, "FlatBlack_Backgrounds", ref Settings.options.Backgrounds, draw);
        DrawToggle(width, ref y, "FlatBlack_Icons", ref Settings.options.Icons, draw);
        DrawToggle(width, ref y, "FlatBlack_Highlights", ref Settings.options.Highlights, draw);
        DrawOpacity(width, ref y, "FlatBlack_WindowOpacity", ref Settings.options.WindowOpacity, draw);
        DrawOpacity(width, ref y, "FlatBlack_PanelOpacity", ref Settings.options.PanelOpacity, draw);
        DrawToggle(width, ref y, "FlatBlack_CustomAccents", ref Settings.options.CustomAccents, draw);
        if (Settings.options.CustomAccents)
        {
            float half = (width - 20f) / 2f;
            if (draw)
            {
                DrawAccent(new Rect(0, y, half, 130), "FlatBlack_PrimaryAccent", ref Settings.options.PrimaryAccent);
                DrawAccent(new Rect(half + 20f, y, half, 130), "FlatBlack_SecondaryAccent", ref Settings.options.SecondaryAccent);
            }
            y += 140f;
        }
        y += 8f;
        Rect reset = new(0, y, 180, 32);
        if (draw)
        {
            if (Widgets.ButtonText(reset, "FlatBlack_Reset".Translate()))
                Settings.ResetDefaults();
            SettingTooltip(reset, "FlatBlack_Reset");
        }
        return y + reset.height;
    }

    private static void DrawToggle(float width, ref float y, string label, ref bool value, bool draw)
    {
        if (draw)
        {
            Rect rect = new(0, y, width, 28);
            Widgets.CheckboxLabeled(rect, label.Translate(), ref value);
            SettingTooltip(rect, label);
        }
        y += 30f;
    }

    private void DrawFontPreset(float width, ref float y, bool draw)
    {
        const float height = 32f;
        if (draw)
        {
            float labelWidth = Math.Min(180f, width * 0.35f);
            Rect row = new(0, y, width, height);
            Widgets.Label(new Rect(0, y + 3f, labelWidth - 8f, height), "FlatBlack_FontPreset".Translate());
            Widgets.Dropdown(new Rect(labelWidth, y, width - labelWidth, height), Settings,
                settings => settings.options.FontPreset, FontMenu, FontLabel(Settings.options.FontPreset));
            SettingTooltip(row, "FlatBlack_FontPreset");
        }
        y += height + 8f;
    }

    private static string FontLabel(ThemeFontPreset preset) => ("FlatBlack_Font_" + preset).Translate();

    private static IEnumerable<Widgets.DropdownMenuElement<ThemeFontPreset>> FontMenu(ThemeSettings settings) =>
        FontPresets.Select(preset => new Widgets.DropdownMenuElement<ThemeFontPreset>
        {
            payload = preset,
            option = new FloatMenuOption(FontLabel(preset), () => settings.options.FontPreset = preset)
        });

    private static void DrawOpacity(float width, ref float y, string label, ref float value, bool draw)
    {
        if (draw)
        {
            Widgets.Label(new Rect(0, y, width, 26), label.Translate(value.ToString("P0")));
            value = Widgets.HorizontalSlider(new Rect(0, y + 24f, width, 20), value, 0f, 1f);
            SettingTooltip(new Rect(0, y, width, 44), label);
        }
        y += 48f;
    }

    private static void DrawAccent(Rect rect, string label, ref Color color)
    {
        Widgets.Label(new Rect(rect.x, rect.y, rect.width - 30f, 28), label.Translate());
        Widgets.DrawBoxSolid(new Rect(rect.xMax - 26f, rect.y, 24, 24), color);
        for (int channel = 0; channel < ColorChannels.Length; channel++)
        {
            float y = rect.y + 34f + channel * 30f;
            Widgets.Label(new Rect(rect.x, y, 94, 26), ColorChannels[channel].Translate((int)MathF.Round(color[channel] * 255f)));
            color[channel] = Widgets.HorizontalSlider(new Rect(rect.x + 98f, y + 4f, rect.width - 102f, 20),
                color[channel], 0f, 1f, roundTo: 1f / 255f);
        }
        SettingTooltip(rect, label);
    }

    private static void SettingTooltip(Rect rect, string label) => TooltipHandler.TipRegionByKey(rect, label + "Tip");
}
