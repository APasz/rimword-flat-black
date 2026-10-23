using System.Reflection;
using HarmonyLib;
using LudeonTK;
using UnityEngine;
using Verse;

namespace FlatBlack.Tests;

public sealed class SmokeMod : Mod
{
    private static int renderedFrames;
    private static bool ready;
    private static bool checkedOption = true;
    private static bool uncheckedOption;
    private static ThemeFontPreset startupFontPreset;
    private static Texture2D? trackedTexture;
    private static int trackedWrites;
    private static string text = "Vanilla font and text input";
    private static readonly (string Label, FieldInfo Field)[] ButtonStates =
    [
        ("Normal", AccessTools.Field(typeof(Widgets), nameof(Widgets.ButtonBGAtlas))),
        ("Hover", AccessTools.Field(typeof(Widgets), "ButtonBGAtlasMouseover")),
        ("Pressed", AccessTools.Field(typeof(Widgets), nameof(Widgets.ButtonBGAtlasClick)))
    ];
    private static FlatBlackMod ThemeMod => LoadedModManager.GetMod<FlatBlackMod>();

    public SmokeMod(ModContentPack content) : base(content)
    {
        LongEventHandler.ExecuteWhenFinished(() =>
        {
            try
            {
                Verify();
            }
            catch (Exception exception)
            {
                Log.Error($"[Flat Black smoke] FAIL {exception}");
                Application.Quit();
            }
        });
    }

    private static void Verify()
    {
        startupFontPreset = GenCommandLine.CommandLineArgPassed("flatblackvanillafonttest")
            ? ThemeFontPreset.Vanilla : ThemeSettings.Defaults.FontPreset;
        Check(ThemeMod.Settings.options.FontPreset == startupFontPreset, "startup uses the default or explicitly saved font preset");
        ThemeMod.Settings.ResetDefaults();
        ThemeMod.ApplyOptions();
        ThemeDef theme = DefDatabase<ThemeDef>.AllDefs.Single();
        theme.Validate();
        FieldInfo fill = AccessTools.Field(typeof(Widgets), nameof(Widgets.WindowBGFillColor));
        Color expected = theme.colors.Single(entry => entry.ResolveField() == fill).value;
        Check(ThemeResources.ResolveColor(Widgets.WindowBGFillColor, fill.FieldHandle).Equals(expected), "window palette");
        Check(!Widgets.WindowBGFillColor.Equals(expected), "readonly vanilla palette remains unchanged");
        VerifyPixelOrientation();

        Texture2D selected = ThemeMod.Content.GetContentHolder<Texture2D>().Get("FlatBlack/UI/Widgets/ButtonBG");
        FieldInfo button = AccessTools.Field(typeof(Widgets), nameof(Widgets.ButtonBGAtlas));
        ModContentPack? other = LoadedModManager.RunningMods.SingleOrDefault(mod => mod.PackageId == "apasz.flatblack.smokeoverride");
        if (other is not null)
            selected = other.GetContentHolder<Texture2D>().Get("UI/Widgets/ButtonBG");
        Texture2D resolved = ThemeResources.ResolveTexture(Widgets.ButtonBGAtlas, button.FieldHandle);
        Check(other is null ? resolved.mipmapCount == 1 && resolved.GetPixels().SequenceEqual(ThemeResources.ReadTexturePixels(selected))
            : ReferenceEquals(resolved, selected), "normal content precedence");
        if (other is null)
            Check(resolved.GetPixels().All(pixel => pixel.r == pixel.g && pixel.g == pixel.b && pixel.a == 1f),
                "default button atlases retain neutral colours after packing");
        Check(!ReferenceEquals(Widgets.ButtonBGAtlas, ThemeMod.Content.GetContentHolder<Texture2D>().Get("FlatBlack/UI/Widgets/ButtonBG")),
            "theme assets do not replace native resources during loading");
        VerifyResourceLoading(theme);
        VerifySettings(theme, other is not null);
        ThemeMod.Settings.options.FontPreset = startupFontPreset;
        ThemeMod.ApplyOptions();

        Check(new Harmony(ThemePatches.HarmonyId).GetPatchedMethods().Any(), "theme patches installed");
        if (GenCommandLine.CommandLineArgPassed("flatblackbaketest"))
            BakeTestPack();
        Log.Message("[Flat Black smoke] PASS boot and resource compatibility checks");
        if (GenCommandLine.CommandLineArgPassed("headless"))
        {
            Application.Quit();
            return;
        }

        ready = true;
        if (GenCommandLine.CommandLineArgPassed("flatblackgameplaytest"))
        {
            GameplaySmoke.Install();
            return;
        }
        new Harmony("FlatBlack.Tests.Screenshot").Patch(AccessTools.Method(typeof(UIRoot_Entry), nameof(UIRoot_Entry.UIRootOnGUI)),
            postfix: new HarmonyMethod(typeof(SmokeMod), nameof(DrawPreview)));
    }

    internal static void VerifyFonts()
    {
        Check((Text.fontStyles[(int)GameFont.Small].font.name == "FlatBlack/Exo2-Regular")
            == (startupFontPreset == ThemeFontPreset.OxaniumExo2), "the startup font preset applies on the first UI frame");
        ThemeMod.Settings.options.FontPreset = ThemeFontPreset.Vanilla;
        ThemeMod.ApplyOptions();
        GUIStyle[][] styles = [Text.fontStyles, Text.textFieldStyles, Text.textAreaStyles, Text.textAreaReadOnlyStyles];
        Font[][] originals = styles.Select(group => group.Select(style => style.font).ToArray()).ToArray();
        GameFont[] sizes = Enum.GetValues<GameFont>();
        float[] heights = sizes.Select(Text.LineHeightOf).ToArray();
        var menu = AccessTools.Method(typeof(FlatBlackMod), "FontMenu")
            .CreateDelegate<Func<ThemeSettings, IEnumerable<Widgets.DropdownMenuElement<ThemeFontPreset>>>>()(ThemeMod.Settings).ToArray();
        Check(menu.Select(item => item.payload).SequenceEqual(Enum.GetValues<ThemeFontPreset>()), "font dropdown contains exactly both presets");
        menu.Single(item => item.payload == ThemeFontPreset.OxaniumExo2).option.action();
        ThemeMod.ApplyOptions();
        Check(styles.All(group => group.Select((style, index) => style.font.GameFontIndex >= sizes.Length
            && style.font.name == (index == (int)GameFont.Medium ? "FlatBlack/Oxanium-Medium" : "FlatBlack/Exo2-Regular")).All(value => value)),
            "bundled fonts register at every standard text size and input style");
        foreach (GameFont size in Enum.GetValues<GameFont>())
        {
            GUIStyle style = Text.fontStyles[(int)size];
            Check(style.font.fontSize == originals[0][(int)size].fontSize, $"{size} retains the native font size");
            string missing = new("AéΩЖ%[]°×−–—…←→✓中한".Where(character => !style.font.GetCharacterInfo(character, out _, style.font.fontSize)).ToArray());
            Check(missing.Length == 0, $"{size} retains symbols and multilingual fallback glyphs (missing: {missing})");
            Check(Text.LineHeightOf(size) == style.CalcHeight(new GUIContent("W"), 999f), $"{size} line height matches native measurement");
        }
        Font[] cached = Text.fontStyles.Select(style => style.font).ToArray();
        ThemeMod.WriteSettings();
        Check(ThemeMod.Settings.options.FontPreset == ThemeFontPreset.OxaniumExo2, "the themed preset remains selected when settings are written");
        menu.Single(item => item.payload == ThemeFontPreset.Vanilla).option.action();
        ThemeMod.ApplyOptions();
        Check(styles.Select((group, index) => group.Select(style => style.font).SequenceEqual(originals[index])).All(value => value)
            && Enum.GetValues<GameFont>().Select(Text.LineHeightOf).SequenceEqual(heights), "vanilla restores exact original fonts and line heights");
        ThemeMod.Settings.options.FontPreset = ThemeFontPreset.OxaniumExo2;
        ThemeMod.ApplyOptions();
        Check(Text.fontStyles.Select(style => style.font).SequenceEqual(cached), "font switching reuses the registered native fonts");
        ThemeMod.Settings.ResetDefaults();
        ThemeMod.ApplyOptions();
    }

    private static void VerifyResourceLoading(ThemeDef theme)
    {
        TextureOverride normal = theme.textures.Single(entry => entry.fieldName == nameof(Widgets.ButtonBGAtlas));
        TextureOverride hover = theme.textures.Single(entry => entry.fieldName == "ButtonBGAtlasMouseover");
        Texture2D normalSource = ContentFinder<Texture2D>.Get(normal.texturePath);
        ThemeResources.ReadTexturePixels(normalSource);
        var fields = theme.textures.Select(entry => entry.ResolveField()).ToArray();
        Texture2D[] previous = fields.Select(ThemeResources.ResolveTextureField).ToArray();
        var unreadable = new Texture2D(4, 4, TextureFormat.RGBA32, mipChain: true);
        try
        {
            unreadable.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            var incomplete = new ThemeDef { defName = "FlatBlack_LoadFailureTest", textures = [normal, hover] };
            ulong bytes = Waylith.Rendering.TexturePixelData.CurrentCpuBytes;
            bool failed = false;
            try
            {
                ThemeResources.Load(incomplete, path => path == normal.texturePath ? normalSource : unreadable);
            }
            catch (InvalidOperationException)
            {
                failed = true;
            }
            Check(failed, "unreadable atlas preparation fails explicitly");
            Check(fields.Select(ThemeResources.ResolveTextureField).SequenceEqual(previous),
                "failed resource loading preserves the active theme");
            Check(Waylith.Rendering.TexturePixelData.CurrentCpuBytes == bytes,
                "failed resource loading releases partially prepared texture storage");

            var overridden = new ThemeDef { defName = "FlatBlack_OverrideTest", textures = [normal] };
            ThemeResources.Load(overridden, _ => unreadable, (_, _) => Texture2D.whiteTexture);
            ThemeResources.Apply(ThemeMod.Settings.options);
            Check(ReferenceEquals(ThemeResources.ResolveTextureField(normal.ResolveField()), Texture2D.whiteTexture),
                "external overrides do not prepare unused theme atlases");
        }
        finally
        {
            var findOverride = AccessTools.Method(typeof(FlatBlackMod), "FindTextureOverride")
                .CreateDelegate<Func<TextureOverride, Texture2D, Texture2D?>>(ThemeMod);
            ThemeResources.Load(theme, path => ContentFinder<Texture2D>.Get(path), findOverride);
            ThemeResources.Apply(ThemeMod.Settings.options);
            UnityEngine.Object.Destroy(unreadable);
        }
    }

    private static void VerifyPixelOrientation()
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
        try
        {
            Color[] pixels = [Color.red, Color.green, Color.blue, new Color(1f, 1f, 1f, 0f)];
            texture.SetPixels(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            Check(ThemeResources.ReadTexturePixels(texture).SequenceEqual(pixels),
                "texture readback preserves channel order, alpha, and vertical orientation");
        }
        finally
        {
            UnityEngine.Object.Destroy(texture);
        }
    }

    private static void VerifySettings(ThemeDef theme, bool hasOverride)
    {
        var originals = theme.textures.ToDictionary(entry => entry.ResolveField(), entry => (Texture2D)entry.ResolveField().GetValue(null)!);
        var defaults = originals.ToDictionary(pair => pair.Key, pair => ThemeResources.ResolveTexture(pair.Value, pair.Key.FieldHandle));
        Check(theme.textures.All(entry =>
        {
            if (hasOverride && entry.fieldName == nameof(Widgets.ButtonBGAtlas))
                return true;
            Texture2D source = ContentFinder<Texture2D>.Get(entry.texturePath);
            Texture2D resolved = defaults[entry.ResolveField()];
            return entry.mipmaps || source.mipmapCount <= 1 ? ReferenceEquals(resolved, source)
                : resolved.mipmapCount == 1 && resolved.GetPixels().SequenceEqual(ThemeResources.ReadTexturePixels(source))
                    && resolved.filterMode == source.filterMode && resolved.wrapMode == source.wrapMode
                    && resolved.anisoLevel == source.anisoLevel && resolved.mipMapBias == source.mipMapBias
                    && resolved.isDataSRGB == source.isDataSRGB;
        }), "default texture coverage and atlas sampling preserve source artwork");
        ThemeOptions baseline = ThemeMod.Settings.options;
        ThemeMod.Settings.options.Backgrounds = false;
        ThemeMod.Settings.options.Icons = false;
        ThemeMod.Settings.options.Highlights = false;
        ThemeMod.ApplyOptions();
        Check(originals.All(pair => ReferenceEquals(ThemeResources.ResolveTexture(pair.Value, pair.Key.FieldHandle),
            hasOverride && pair.Key.Name == nameof(Widgets.ButtonBGAtlas) ? ContentFinder<Texture2D>.Get("UI/Widgets/ButtonBG") : pair.Value)),
            "all disabled texture components restore original resources");
        Check(theme.colors.All(entry =>
        {
            FieldInfo field = entry.ResolveField();
            Color original = (Color)field.GetValue(null)!;
            return ThemeResources.ResolveColor(original, field.FieldHandle).Equals(original);
        }), "all disabled colour components restore original resources");

        ThemeMod.Settings.options = baseline;
        ThemeMod.Settings.options.WindowOpacity = 0.25f;
        ThemeMod.Settings.options.PanelOpacity = 0.2f;
        ThemeMod.Settings.options.CustomAccents = true;
        ThemeMod.Settings.options.PrimaryAccent = Color.cyan;
        ThemeMod.Settings.options.SecondaryAccent = Color.yellow;
        Texture2D source = ThemeMod.Content.GetContentHolder<Texture2D>().Get("FlatBlack/UI/Widgets/ButtonBGClick");
        Color[] originalPixels = source.GetPixels();
        FieldInfo click = AccessTools.Field(typeof(Widgets), nameof(Widgets.ButtonBGAtlasClick));
        ThemeMod.ApplyOptions();
        Texture2D customized = ThemeResources.ResolveTexture(Widgets.ButtonBGAtlasClick, click.FieldHandle);
        Check(!ReferenceEquals(customized, source) && !customized.GetPixels().SequenceEqual(originalPixels), "custom accents and opacity transform owned textures");
        Check(customized.mipmapCount == 1, "customized button atlases remain free of mipmap seams");
        Check(customized.anisoLevel == source.anisoLevel && customized.mipMapBias == source.mipMapBias,
            "settings variants preserve source sampler parameters");
        Check(source.GetPixels().SequenceEqual(originalPixels), "customization leaves source texture pixels unchanged");
        Check(ReferenceEquals(ThemeResources.ResolveTexture(Texture2D.whiteTexture, click.FieldHandle), Texture2D.whiteTexture),
            "later field replacements are preserved");
        FieldInfo fill = AccessTools.Field(typeof(Widgets), nameof(Widgets.WindowBGFillColor));
        Check(Math.Abs(ThemeResources.ResolveColor(Widgets.WindowBGFillColor, fill.FieldHandle).a - 0.25f) < 0.0001f, "window opacity applies live");
        ThemeMod.Settings.options.PanelOpacity = 0.3f;
        ThemeMod.ApplyOptions();
        Check(ReferenceEquals(customized, ThemeResources.ResolveTexture(Widgets.ButtonBGAtlasClick, click.FieldHandle)), "texture variants are reused when settings change");
        VerifyNeutralTextureCache();
        Check(theme.textures.Where(entry => !entry.recolor && entry.opacity == OpacityChannel.None).All(entry =>
        {
            FieldInfo field = entry.ResolveField();
            return ReferenceEquals(ThemeResources.ResolveTexture(originals[field], field.FieldHandle), defaults[field]);
        }), "semantic icon colours stay unchanged by custom accents");
        if (hasOverride)
        {
            FieldInfo button = AccessTools.Field(typeof(Widgets), nameof(Widgets.ButtonBGAtlas));
            Check(ReferenceEquals(ThemeResources.ResolveTexture(Widgets.ButtonBGAtlas, button.FieldHandle), ContentFinder<Texture2D>.Get("UI/Widgets/ButtonBG")),
                "later mod texture takes precedence over custom settings");
        }
        ThemeMod.Settings.ResetDefaults();
        ThemeMod.ApplyOptions();
        Check(ThemeMod.Settings.options == baseline && defaults.All(pair =>
            ReferenceEquals(ThemeResources.ResolveTexture(originals[pair.Key], pair.Key.FieldHandle), pair.Value)), "reset restores exact default texture references");
        ThemeMod.WriteSettings();
    }

    private static void VerifyNeutralTextureCache()
    {
        FieldInfo field = AccessTools.Field(typeof(Widgets), nameof(Widgets.ButtonSubtleAtlas));
        Texture2D neutral = ThemeResources.ResolveTextureField(field);
        Color originalAccent = ThemeMod.Settings.options.PrimaryAccent;
        var tracker = new Harmony("FlatBlack.Tests.TextureWrites");
        MethodInfo write = AccessTools.Method(typeof(Texture2D), nameof(Texture2D.SetPixels), [typeof(Color[])]);
        trackedTexture = neutral;
        trackedWrites = 0;
        try
        {
            tracker.Patch(write, prefix: new HarmonyMethod(typeof(SmokeMod), nameof(CountTextureWrite)));
            neutral.SetPixels(neutral.GetPixels());
            Check(trackedWrites == 1, "native texture write tracking observes real pixel updates");
            trackedWrites = 0;
            ThemeMod.Settings.options.PrimaryAccent = Color.magenta;
            ThemeMod.ApplyOptions();
            Check(trackedWrites == 0 && ReferenceEquals(neutral, ThemeResources.ResolveTextureField(field)),
                "accent changes reuse neutral textures without rewriting their pixels");
        }
        finally
        {
            tracker.Unpatch(write, HarmonyPatchType.All, tracker.Id);
            trackedTexture = null;
            ThemeMod.Settings.options.PrimaryAccent = originalAccent;
            ThemeMod.ApplyOptions();
        }
    }

    private static void CountTextureWrite(Texture2D __instance)
    {
        if (ReferenceEquals(__instance, trackedTexture))
            trackedWrites++;
    }

    private static void BakeTestPack()
    {
        Type baker = typeof(Texture2D).Assembly.GetType("UnityStubs.ModAssetPacks", throwOnError: true)!;
        MethodInfo method = baker.GetMethod("BakeForUpload", BindingFlags.Public | BindingFlags.Static)!;
        string folder = Path.Combine(ThemeMod.Content.RootDir, "1.7");
        object result = method.Invoke(null, [folder, new Action<string>(_ => { }), CancellationToken.None])!;
        Check((bool)result.GetType().GetProperty("Succeeded")!.GetValue(result)!, "native texture pack baking succeeds");
        Check(File.Exists(Path.Combine(folder, "Assets.zip")), "native texture pack exists");
    }

    private static void DrawPreview()
    {
        if (!ready || Event.current.type != EventType.Repaint)
            return;

        renderedFrames++;
        if (renderedFrames == 1)
        {
            Screen.SetResolution(1280, 900, false);
            VerifyFonts();
        }
        foreach (Window window in Find.WindowStack.Windows.OfType<EditWindow_Log>().ToArray())
            Find.WindowStack.TryRemove(window, false);
        if (renderedFrames is >= 25 and < 45)
            DrawControls();
        if (renderedFrames == 65)
        {
            ThemeMod.Settings.options.CustomAccents = true;
            ThemeMod.Settings.options.PrimaryAccent = Color.cyan;
            ThemeMod.Settings.options.SecondaryAccent = Color.yellow;
            ThemeMod.Settings.options.WindowOpacity = 0.6f;
            ThemeMod.Settings.options.PanelOpacity = 0.4f;
            ThemeMod.ApplyOptions();
        }
        if (renderedFrames == 85)
        {
            AccessTools.Field(typeof(FlatBlackMod), "settingsScroll").SetValue(ThemeMod, Vector2.zero);
            ThemeMod.Settings.options.Backgrounds = false;
            ThemeMod.Settings.options.Icons = false;
            ThemeMod.Settings.options.Highlights = false;
            ThemeMod.ApplyOptions();
        }
        if (renderedFrames == 81)
            AccessTools.Field(typeof(FlatBlackMod), "settingsScroll").SetValue(ThemeMod, new Vector2(0, 10000));
        if (renderedFrames == 101)
        {
            ThemeMod.Settings.ResetDefaults();
            ThemeMod.ApplyOptions();
        }
        if (renderedFrames == 106)
        {
            var preview = (ThemePreview)AccessTools.Field(typeof(FlatBlackMod), "preview").GetValue(ThemeMod)!;
            AccessTools.Field(typeof(ThemePreview), "scroll").SetValue(preview, new Vector2(0, 10000));
        }
        if (renderedFrames == 115)
        {
            ThemeMod.Settings.options.FontPreset = ThemeFontPreset.OxaniumExo2;
            ThemeMod.ApplyOptions();
            AccessTools.Field(typeof(FlatBlackMod), "settingsScroll").SetValue(ThemeMod, Vector2.zero);
            var preview = (ThemePreview)AccessTools.Field(typeof(FlatBlackMod), "preview").GetValue(ThemeMod)!;
            AccessTools.Field(typeof(ThemePreview), "scroll").SetValue(preview, Vector2.zero);
        }
        if (renderedFrames is >= 45 and < 135)
        {
            bool compact = renderedFrames is >= 101 and < 115;
            Rect window = new(40, 40, Math.Min(Screen.width - 80, compact ? 520 : 900),
                compact ? Math.Min(Screen.height - 80, 550) : Screen.height - 80);
            Widgets.DrawWindowBackground(window);
            DrawSettings(window.ContractedBy(18));
        }
        if (renderedFrames >= 135)
            DrawFontSamples();
        if (renderedFrames is 20 or 40 or 60 or 80 or 83 or 100 or 105 or 108 or 130 or 150)
        {
            string name = renderedFrames switch
            {
                20 => "menu.png",
                40 => "controls.png",
                60 => "settings.png",
                80 => "settings-custom.png",
                83 => "settings-controls-bottom.png",
                100 => "settings-vanilla.png",
                105 => "settings-compact.png",
                130 => "settings-fonts.png",
                150 => "font-controls.png",
                _ => "settings-compact-bottom.png"
            };
            Capture(name);
        }
        if (renderedFrames >= 155)
        {
            ThemeMod.Settings.ResetDefaults();
            ThemeMod.WriteSettings();
            Log.Message("[Flat Black smoke] PASS rendered vanilla controls");
            Application.Quit();
        }
    }

    private static void DrawFontSamples()
    {
        Color previousColor = GUI.color;
        GameFont previousFont = Text.Font;
        TextAnchor previousAnchor = Text.Anchor;
        bool previousWordWrap = Text.WordWrap;
        try
        {
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            Widgets.DrawWindowBackground(new Rect(40, 40, 1100, 730));
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(60, 60, 1060, 40), "Oxanium headings + Exo 2 body text");
            foreach (GameFont size in Enum.GetValues<GameFont>())
            {
                Text.Font = size;
                float y = 120 + (int)size * 95;
                Widgets.Label(new Rect(60, y, 1060, 40), $"{size}: Construction: 14 · Manipulation: 82% · Temperature: 21°C · 3.5 kg / 35 kg");
                Widgets.Label(new Rect(60, y + 40, 1060, 40), "café · Ελληνικά · Русский · 中文 · 한국어 · [ ] × − – — … ← → ✓");
            }
            Text.Font = GameFont.Small;
            Widgets.ButtonText(new Rect(60, 430, 500, 32), "Normal button");
            Widgets.ButtonText(new Rect(580, 430, 500, 32), "Disabled button", active: false);
            text = Widgets.TextField(new Rect(60, 485, 1020, 32), text);
            Widgets.DrawMenuSection(new Rect(60, 540, 1020, 150));
            Widgets.Label(new Rect(80, 555, 980, 110), "Long labels and wrapped text use the same font for drawing and measurement.\n<color=green>Healthy text</color> · <color=red>Warning text</color> · <b>Bold</b> · <i>Italic</i>");
        }
        finally
        {
            GUI.color = previousColor;
            Text.Font = previousFont;
            Text.Anchor = previousAnchor;
            Text.WordWrap = previousWordWrap;
        }
    }

    private static void DrawSettings(Rect rect)
    {
        Color previousColor = GUI.color;
        GameFont previousFont = Text.Font;
        TextAnchor previousAnchor = Text.Anchor;
        bool previousWordWrap = Text.WordWrap;
        Color callerColor = new(0.4f, 0.5f, 0.6f, 0.7f);
        try
        {
            GUI.color = callerColor;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleRight;
            Text.WordWrap = false;
            ThemeMod.DoSettingsWindowContents(rect);
            if (renderedFrames == 60)
                Check(GUI.color.Equals(callerColor) && Text.Font == GameFont.Tiny && Text.Anchor == TextAnchor.MiddleRight && !Text.WordWrap,
                    "settings and preview restore the caller's GUI state");
        }
        finally
        {
            GUI.color = previousColor;
            Text.Font = previousFont;
            Text.Anchor = previousAnchor;
            Text.WordWrap = previousWordWrap;
        }
    }

    private static void DrawControls()
    {
        Color originalColor = GUI.color;
        GameFont originalFont = Text.Font;
        TextAnchor originalAnchor = Text.Anchor;
        try
        {
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Rect panel = new(60, 100, 640, 680);
            Widgets.DrawWindowBackground(panel);
            GUI.BeginGroup(panel.ContractedBy(24));
            Widgets.Label(new Rect(0, 0, 580, 30), "Flat Black · RimWorld 1.7 standalone");
            Widgets.DrawMenuSection(new Rect(0, 48, 592, 244));
            Widgets.ButtonText(new Rect(16, 64, 270, 36), "Standard button");
            Widgets.ButtonText(new Rect(306, 64, 270, 36), "Disabled button", active: false);
            Widgets.CheckboxLabeled(new Rect(16, 120, 270, 30), "Checked", ref checkedOption);
            Widgets.CheckboxLabeled(new Rect(306, 120, 270, 30), "Unchecked", ref uncheckedOption);
            Widgets.RadioButtonLabeled(new Rect(16, 170, 270, 30), "Selected radio", true);
            Widgets.RadioButtonLabeled(new Rect(306, 170, 270, 30), "Unselected radio", false);
            text = Widgets.TextField(new Rect(16, 218, 560, 32), text);
            Widgets.CheckboxMulti(new Rect(16, 260, 24, 24), MultiCheckboxState.Partial);
            Widgets.Label(new Rect(48, 260, 528, 26), "Partial selection");
            Widgets.DrawOptionBackground(new Rect(0, 300, 592, 48), true);
            Widgets.Label(new Rect(16, 312, 560, 30), "Selected option");
            Widgets.DrawOptionBackground(new Rect(0, 360, 592, 48), false);
            Widgets.Label(new Rect(16, 372, 560, 30), "Unselected option");
            Color[] colors = [Color.white, Color.gray, Color.red, Color.green, Color.yellow, new Color(1, 1, 1, 0.4f)];
            string[] labels = ["Normal", "Disabled", "Warning", "Healthy", "Caution", "Faded"];
            for (int index = 0; index < colors.Length; index++)
            {
                GUI.color = colors[index];
                Widgets.Label(new Rect(index * 96, 448, 96, 30), labels[index]);
            }
            GUI.color = Color.white;
            Widgets.Label(new Rect(0, 508, 592, 60), "Meaningful text colours, UI sizes, and window behaviour are preserved.");
            Text.Anchor = TextAnchor.MiddleCenter;
            const float buttonGap = 16;
            float buttonWidth = (592 - buttonGap * (ButtonStates.Length - 1)) / ButtonStates.Length;
            for (int index = 0; index < ButtonStates.Length; index++)
            {
                (string label, FieldInfo field) = ButtonStates[index];
                Rect button = new(index * (buttonWidth + buttonGap), 584, buttonWidth, 36);
                Widgets.DrawAtlas(button, ThemeResources.ResolveTextureField(field));
                Widgets.Label(button, label);
            }
            GUI.EndGroup();
        }
        finally
        {
            GUI.color = originalColor;
            Text.Font = originalFont;
            Text.Anchor = originalAnchor;
        }
    }

    internal static void Capture(string name)
    {
        ScreenCapture.CaptureScreenshot(Path.Combine(GenFilePaths.SaveDataFolderPath, name));
        Log.Message($"[Flat Black smoke] Capturing {name}");
    }

    internal static void Check(bool condition, string description)
    {
        if (!condition)
        {
            Application.Quit();
            throw new InvalidOperationException($"[Flat Black smoke] FAIL {description}");
        }
        Log.Message($"[Flat Black smoke] PASS {description}");
    }
}
