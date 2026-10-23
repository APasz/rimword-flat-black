using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Xml;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace FlatBlack.Tests;

internal static class ThemeTests
{
    private const string TestOwner = "FlatBlack.Tests.Theme";
    private const string OtherOwner = "FlatBlack.Tests.OtherMod";
    private static readonly Color CustomColor = new(0.3f, 0.4f, 0.5f, 0.6f);
    private static readonly FieldInfo TestField = typeof(FloatMenuOption).GetField(nameof(FloatMenuOption.ColorBGActive))!;
    private static Font? rejectedFont;

    internal static int Run(string repository)
    {
        (string Name, Action Test)[] tests =
        [
            ("native XML loading and complete asset manifest", () => Manifest(repository)),
            ("invalid resource definitions fail explicitly", InvalidDefinitions),
            ("settings use theme defaults and persist across reloads", () => SettingsPersistence(repository)),
            ("invalid settings fail explicitly", () => InvalidSettings(repository)),
            ("font switching restores styles and respects later mods", FontSwitching),
            ("accents preserve neutral and semantic colours", () => AccentColors(repository)),
            ("component toggles restore original colours immediately", () => ComponentToggles(repository)),
            ("preview leaves settings usable at narrow and short window sizes", PreviewLayout),
            ("readonly fields stay unchanged and later colours are respected", PaletteResolution),
            ("field discovery stays within vanilla managed methods", () => PatchTargets(repository)),
            ("transpiler preserves labels, exception blocks, and unrelated instructions", TranspilerMetadata),
            ("real Harmony patch preserves another mod's transpiler and prefix", PatchCompatibility)
        ];

        foreach ((string name, Action test) in tests)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                test();
                Console.WriteLine($"PASS {name} ({timer.ElapsedMilliseconds} ms)");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {name}: {exception}");
                return 1;
            }
        }
        return 0;
    }

    private static ThemeDef ReadTheme(string repository)
    {
        var document = new XmlDocument();
        document.Load(Path.Combine(repository, "1.7", "Defs", "Theme.xml"));
        XmlNode node = document.DocumentElement!.FirstChild!;
        ThemeDef result = DirectXmlToObject.ObjectFromXml<ThemeDef>(node, false);
        DirectXmlCrossRefLoader.ResolveAllWantedCrossReferences(FailMode.LogErrors);
        return result;
    }

    private static void Manifest(string repository)
    {
        ThemeDef theme = ReadTheme(repository);
        theme.Validate();
        Check(theme.textures.Count > 0 && theme.colors.Count > 0, "The skin must contain textures and colours.");

        HashSet<string> paths = theme.textures.Select(entry => entry.texturePath).ToHashSet(StringComparer.Ordinal);
        HashSet<string> files = Directory.GetFiles(Path.Combine(repository, "1.7", "Textures"), "*.png", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Path.Combine(repository, "1.7", "Textures"), path).Replace('\\', '/')[..^4])
            .ToHashSet(StringComparer.Ordinal);
        Check(paths.SetEquals(files), "Every declared texture must exist, and every packaged texture must be used.");
        Check(paths.All(path => path.StartsWith("FlatBlack/", StringComparison.Ordinal)), "The mod must not replace native asset paths before settings can be applied.");
        Check(theme.colors.All(entry => entry.declaringType == typeof(Widgets) || entry.declaringType == typeof(GenUI)
            || entry.declaringType == typeof(FloatMenuOption)), "Health and social status colours must retain their meaning.");
        Check(theme.colors.All(entry => entry.fieldName != nameof(FloatMenuOption.ColorTextDisabled)), "Disabled text must retain its vanilla styling.");
        Check(theme.textures.All(entry => entry.ResolveField().IsInitOnly), "The test should cover the game's readonly UI fields.");
        Check(theme.textures.Where(entry => entry.declaringType == typeof(LogEntry) || entry.declaringType == typeof(RimWorld.WidgetsWork))
            .All(entry => !entry.recolor), "Battle/status and work priority textures must keep their meaningful colours.");
        Check(theme.textures.Where(entry => entry.declaringType == typeof(Widgets)
                && entry.fieldName is nameof(Widgets.ButtonBGAtlas) or nameof(Widgets.ButtonBGAtlasClick)
                    or "ButtonBGAtlasMouseover" or nameof(Widgets.ButtonSubtleAtlas))
            .All(entry => !entry.mipmaps), "Sliced button atlases must not blend borders into their centre through mipmaps.");

        var about = new XmlDocument();
        about.Load(Path.Combine(repository, "About", "About.xml"));
        string[] dependencies = about.SelectNodes("/ModMetaData/modDependencies/li/packageId")!.Cast<XmlNode>().Select(node => node.InnerText).ToArray();
        Check(dependencies.SequenceEqual(["brrainz.harmony"]), "Harmony must be the only mod dependency.");
    }

    private static void InvalidDefinitions()
    {
        var missingField = new TextureOverride { declaringType = typeof(Widgets), fieldName = "MissingUiField", texturePath = "UI/Missing" };
        Check(missingField.ValidationError()!.Contains("no longer exists"), "Missing fields should identify the broken binding.");
        var wrongType = new ColorOverride { declaringType = typeof(Widgets), fieldName = nameof(Widgets.ButtonBGAtlas), value = Color.white };
        Check(wrongType.ValidationError() is not null, "A colour cannot target a texture field.");
        var invalidPath = new TextureOverride { declaringType = typeof(Widgets), fieldName = nameof(Widgets.ButtonBGAtlas), component = ThemeComponent.Backgrounds, texturePath = "../UI/Button.png" };
        Check(invalidPath.ValidationError() is not null, "Paths must stay inside the content namespace.");
        invalidPath.texturePath = "UI/Widgets/ButtonBG";
        Check(invalidPath.ValidationError()!.Contains("FlatBlack content namespace"), "Native texture paths must not bypass component settings.");
        invalidPath.texturePath = "FlatBlack/UI/Widgets/ButtonBG";
        invalidPath.component = (ThemeComponent)999;
        Check(invalidPath.ValidationError()!.Contains("component"), "Unknown component names must fail explicitly.");
        invalidPath.component = ThemeComponent.Backgrounds;
        invalidPath.opacity = (OpacityChannel)999;
        Check(invalidPath.ValidationError()!.Contains("opacity"), "Unknown opacity channels must fail explicitly.");
        var invalidColor = new ColorOverride { declaringType = typeof(FloatMenuOption), fieldName = nameof(FloatMenuOption.ColorBGActive) };
        Check(invalidColor.ValidationError() is not null, "Missing colours must not silently become transparent black.");
        var theme = new ThemeDef { defName = "Duplicate", colors = [PaletteEntry(), PaletteEntry()] };
        Expect<InvalidOperationException>(theme.Validate);
    }

    private static ColorOverride PaletteEntry() => new()
    {
        declaringType = typeof(FloatMenuOption),
        fieldName = nameof(FloatMenuOption.ColorBGActive),
        component = ThemeComponent.Backgrounds,
        value = Color.black
    };

    private static void SettingsPersistence(string repository)
    {
        ThemeDef theme = ReadTheme(repository);
        ThemeSettings.ConfigureDefaults(theme);
        var settings = new ThemeSettings();
        Check(settings.options == theme.DefaultOptions(), "A fresh settings file must reproduce the existing theme.");
        Check(settings.options.FontPreset == ThemeFontPreset.OxaniumExo2, "New settings must use Oxanium + Exo 2.");
        settings.options.WindowOpacity = 0.25f;
        settings.options.PanelOpacity = 0.4f;
        settings.options.Icons = false;
        settings.options.Highlights = false;
        settings.options.CustomAccents = true;
        settings.options.PrimaryAccent = Color.cyan;
        settings.options.SecondaryAccent = Color.yellow;
        settings.options.FontPreset = ThemeFontPreset.Vanilla;
        string file = Path.Combine(Path.GetTempPath(), $"flatblack-settings-{Guid.NewGuid():N}.xml");
        try
        {
            SaveSettings(settings, file);
            ThemeSettings loaded = LoadSettings(file);
            Check(loaded.options == settings.options, "Every option must survive native Scribe serialization.");
            loaded.ResetDefaults();
            Check(loaded.options == theme.DefaultOptions(), "Reset must restore all values, including custom accent state.");
            var legacy = new XmlDocument();
            legacy.Load(file);
            legacy.DocumentElement!.RemoveChild(legacy.SelectSingleNode("/Settings/fontPreset")!);
            legacy.Save(file);
            loaded = LoadSettings(file);
            settings.options.FontPreset = theme.DefaultOptions().FontPreset;
            Check(loaded.options == settings.options, "Existing settings without a font preset must retain their values and adopt the current font default.");
            loaded.ResetDefaults();
            SaveSettings(loaded, file);
            legacy.Load(file);
            Check(legacy.SelectSingleNode("/Settings/fontPreset")?.InnerText == nameof(ThemeFontPreset.OxaniumExo2),
                "The default font choice must also be saved explicitly to survive future default changes.");
        }
        finally
        {
            Scribe.ForceStop();
            File.Delete(file);
        }
    }

    private static void SaveSettings(ThemeSettings settings, string path)
    {
        Scribe.saver.InitSaving(path, "Settings");
        settings.ExposeData();
        Scribe.saver.FinalizeSaving();
    }

    private static ThemeSettings LoadSettings(string path)
    {
        var settings = new ThemeSettings();
        Scribe.loader.InitLoading(path);
        settings.ExposeData();
        Scribe.loader.FinalizeLoading();
        return settings;
    }

    private static void InvalidSettings(string repository)
    {
        ThemeOptions options = ReadTheme(repository).DefaultOptions();
        options.WindowOpacity = float.NaN;
        Expect<ArgumentOutOfRangeException>(options.Validate);
        options.WindowOpacity = 1f;
        options.PanelOpacity = 1.1f;
        Expect<ArgumentOutOfRangeException>(options.Validate);
        options.PanelOpacity = 1f;
        options.FontPreset = (ThemeFontPreset)999;
        Expect<ArgumentOutOfRangeException>(options.Validate);
        options.FontPreset = ThemeFontPreset.Vanilla;
        options.PrimaryAccent = new Color(1, 0, 0, 0.5f);
        Expect<ArgumentException>(options.Validate);
    }

    private static void FontSwitching()
    {
        GUIStyle[][] groups = [Text.fontStyles, Text.textFieldStyles, Text.textAreaStyles, Text.textAreaReadOnlyStyles];
        var originals = groups.SelectMany(styles => styles.Select((style, index) => (Styles: styles, Index: index, Style: style, Font: style.font))).ToArray();
        float[] heights = AccessTools.StaticFieldRefAccess<float[]>(typeof(Text), "lineHeights");
        float[] spacing = AccessTools.StaticFieldRefAccess<float[]>(typeof(Text), "spaceBetweenLines");
        float[] originalHeights = heights.ToArray();
        float[] originalSpacing = spacing.ToArray();
        GameFont[] sizes = Enum.GetValues<GameFont>();
        Font[] replacements = sizes.Select(size => new Font { GameFontIndex = ((int)size + 1) % sizes.Length }).ToArray();
        int loads = 0;
        var fonts = new ThemeFonts(() => { loads++; return replacements; });
        var fault = new Harmony(TestOwner + ".FontFailure");
        MethodInfo measure = AccessTools.Method(typeof(GUIStyle), nameof(GUIStyle.CalcHeight), [typeof(GUIContent), typeof(float)]);
        try
        {
            fault.Patch(measure, prefix: new HarmonyMethod(typeof(ThemeTests), nameof(RejectFontMeasurement)));
            fonts.Apply(ThemeFontPreset.Vanilla);
            Check(loads == 0, "Vanilla must not load unused font files.");
            rejectedFont = replacements[(int)GameFont.Small];
            Expect<InvalidOperationException>(() => fonts.Apply(ThemeFontPreset.OxaniumExo2));
            Check(originals.All(binding => ReferenceEquals(binding.Style.font, binding.Font))
                && heights.SequenceEqual(originalHeights) && spacing.SequenceEqual(originalSpacing),
                "Failed font measurement must leave all active fonts and measurements unchanged.");
            rejectedFont = null;
            fault.Unpatch(measure, HarmonyPatchType.All, fault.Id);
            fonts.Apply(ThemeFontPreset.OxaniumExo2);
            Check(originals.All(binding => ReferenceEquals(binding.Styles[binding.Index], binding.Style)
                && ReferenceEquals(binding.Style.font, replacements[binding.Index])), "Font changes must preserve the existing style objects.");
            Check(Enum.GetValues<GameFont>().All(size => Text.LineHeightOf(size) == Text.fontStyles[(int)size].CalcHeight(new GUIContent("W"), 999f)),
                "Cached line heights must match the active fonts.");
            fonts.Apply(ThemeFontPreset.Vanilla);
            Check(originals.All(binding => ReferenceEquals(binding.Style.font, binding.Font))
                && heights.SequenceEqual(originalHeights) && spacing.SequenceEqual(originalSpacing), "Vanilla must restore exact original fonts and measurements.");
            fonts.Apply(ThemeFontPreset.OxaniumExo2);
            Font external = new() { GameFontIndex = 1 };
            Text.fontStyles[1].font = external;
            GUIStyle externalStyle = new(Text.textFieldStyles[1]);
            Text.textFieldStyles[1] = externalStyle;
            heights[1] = 123f;
            fonts.Apply(ThemeFontPreset.Vanilla);
            fonts.Apply(ThemeFontPreset.OxaniumExo2);
            Check(ReferenceEquals(Text.fontStyles[1].font, external) && ReferenceEquals(Text.textFieldStyles[1], externalStyle) && heights[1] == 123f,
                "Later font, style and metric replacements must take precedence.");
            Check(loads == 1, "Repeated switching must reuse font resources.");
            Expect<ArgumentOutOfRangeException>(() => fonts.Apply((ThemeFontPreset)999));
        }
        finally
        {
            rejectedFont = null;
            fault.Unpatch(measure, HarmonyPatchType.All, fault.Id);
            foreach (var binding in originals)
            {
                binding.Styles[binding.Index] = binding.Style;
                binding.Style.font = binding.Font;
            }
            originalHeights.CopyTo(heights, 0);
            originalSpacing.CopyTo(spacing, 0);
        }
    }

    private static void RejectFontMeasurement(GUIStyle __instance)
    {
        if (ReferenceEquals(__instance.font, rejectedFont))
            throw new InvalidOperationException("Injected font measurement failure.");
    }

    private static void AccentColors(string repository)
    {
        ThemeOptions options = ReadTheme(repository).DefaultOptions();
        Color red = new(0.8f, 0, 0, 0.4f);
        Check(options.Recolor(red).Equals(red), "Default accents must preserve the exact source colour.");
        options.CustomAccents = true;
        options.PrimaryAccent = Color.cyan;
        options.SecondaryAccent = Color.yellow;
        Color changed = options.Recolor(red);
        Check(changed.r == 0f && changed.g == 0.8f && changed.b == 0.8f && changed.a == red.a,
            "Accent changes must preserve shading and alpha.");
        foreach (Color semantic in new[] { Color.white, Color.gray, Color.black, Color.green, Color.yellow })
            Check(options.Recolor(semantic).Equals(semantic), "Neutral, green, and yellow colours must stay unchanged.");
        Color purple = options.Recolor(new Color(2f / 3f, 0, 1, 0.6f));
        Check(Math.Abs(purple.r - options.SecondaryAccent.r) < 0.0001f
            && Math.Abs(purple.g - options.SecondaryAccent.g) < 0.0001f
            && Math.Abs(purple.b - options.SecondaryAccent.b) < 0.0001f && purple.a == 0.6f,
            $"Purple decorations must use the secondary accent; received {purple}.");
    }

    private static void ComponentToggles(string repository)
    {
        ThemeDef defaults = ReadTheme(repository);
        ThemeOptions options = defaults.DefaultOptions();
        LoadTestPalette();
        Color original = FloatMenuOption.ColorBGActive;
        ThemeResources.Apply(options);
        Check(ThemeResources.ResolveColor(original, TestField.FieldHandle).Equals(Color.black), "Enabled backgrounds must use the theme.");
        options.Backgrounds = false;
        ThemeResources.Apply(options);
        Check(ThemeResources.ResolveColor(original, TestField.FieldHandle).Equals(original), "Disabling backgrounds must restore vanilla immediately.");
        Check(ThemeResources.ResolveColor(CustomColor, TestField.FieldHandle).Equals(CustomColor), "A later mod's colour must still take precedence.");
        options.Backgrounds = true;
        ThemeResources.Apply(options);
        Check(ThemeResources.ResolveColor(original, TestField.FieldHandle).Equals(Color.black), "Re-enabling backgrounds must restore the theme.");
        Check(Math.Abs(options.OpacityScale(OpacityChannel.Window, defaults) - 1f) < 0.0001f, "Default opacity must leave existing alpha unchanged.");
        options.WindowOpacity = defaults.DefaultOpacity(OpacityChannel.Window) / 2f;
        Check(options.OpacityScale(OpacityChannel.Window, defaults) == 0.5f, "Window opacity must scale proportionally to the theme default.");
    }

    private static void LoadTestPalette()
    {
        var theme = new ThemeDef { defName = "PaletteTest", colors = [PaletteEntry()] };
        ThemeResources.Load(theme, _ => throw new InvalidOperationException("This theme has no texture I/O."));
    }

    private static void PreviewLayout()
    {
        foreach (float width in new[] { 360f, 520f, 900f, 1200f })
        foreach (float height in new[] { 320f, 550f, 900f })
        {
            Rect area = new(40, 60, width, height);
            (Rect controls, Rect preview) = ThemePreview.Arrange(area);
            Check(controls.x == area.x && preview.x == area.x && controls.width == area.width && preview.width == area.width,
                "Both sections must remain inside the settings width.");
            Check(controls.y == area.y && controls.height > 0f && preview.height > 0f && preview.yMax == area.yMax,
                "Both sections must remain visible inside the settings height.");
            Check(controls.yMax < preview.y && preview.height <= area.height / 2f,
                "The preview must not overlap or consume most of the settings area.");
        }
        Check(ThemePreview.Arrange(new Rect(0, 0, 360, 900)).Preview.height
            > ThemePreview.Arrange(new Rect(0, 0, 1200, 900)).Preview.height,
            "Narrow windows must reserve room for wrapped samples.");
    }

    private static void PaletteResolution()
    {
        Color original = FloatMenuOption.ColorBGActive;
        LoadTestPalette();
        Check(FloatMenuOption.ColorBGActive.Equals(original), "Loading the theme must not mutate a readonly game field.");
        Check(ThemeResources.ResolveColor(original, TestField.FieldHandle).Equals(Color.black), "The vanilla colour must receive the theme palette.");
        Check(ThemeResources.ResolveColor(CustomColor, TestField.FieldHandle).Equals(CustomColor), "A later custom colour must take precedence.");
    }

    private static void PatchTargets(string repository)
    {
        ThemeDef theme = ReadTheme(repository);
        var fields = theme.textures.Cast<UiFieldOverride>().Concat(theme.colors).Select(entry => entry.ResolveField()).ToHashSet();
        MethodInfo[] methods = ThemePatches.FindTargets(typeof(Widgets).Assembly, fields).ToArray();
        Check(methods.Length > 0, "There must be matching drawing methods.");
        Check(methods.All(method => method.DeclaringType!.Assembly == typeof(Widgets).Assembly && !method.ContainsGenericParameters), "Only concrete vanilla methods should be patched.");
        Check(methods.Any(method => method.DeclaringType == typeof(Widgets) && method.Name == nameof(Widgets.DrawWindowBackground)), "Window backgrounds must be covered.");
        Check(methods.Any(method => method.DeclaringType == typeof(FloatMenuOption) && method.Name == nameof(FloatMenuOption.DoGUI)), "Float menus must be covered.");
        Check(methods.All(method => method.DeclaringType != typeof(GUI)), "The global engine drawing API must stay untouched.");
        Console.WriteLine($"  Discovered {methods.Length} vanilla resource consumers.");
    }

    private static void TranspilerMetadata()
    {
        LoadTestPalette();
        ILGenerator generator = new DynamicMethod("Labels", typeof(void), Type.EmptyTypes).GetILGenerator();
        Label label = generator.DefineLabel();
        var instruction = new CodeInstruction(OpCodes.Ldsfld, TestField);
        instruction.labels.Add(label);
        instruction.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        instruction.blocks.Add(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
        var unrelated = new CodeInstruction(OpCodes.Ldc_I4_1);
        CodeInstruction[] result = ThemePatches.Transpiler([instruction, unrelated]).ToArray();
        Check(result.Length == 4 && result[0].labels.Contains(label), "Branch targets must still point to the original load.");
        Check(result[0].blocks.Single().blockType == ExceptionBlockType.BeginExceptionBlock
            && result[2].blocks.Single().blockType == ExceptionBlockType.EndExceptionBlock, "The resolver must stay inside the original exception region.");
        Check(instruction.blocks.Count == 2, "The transpiler must not alter another patch's input instructions.");
        Check(ReferenceEquals(result[^1], unrelated), "Unrelated instructions should be passed through unchanged.");
    }

    private static void PatchCompatibility()
    {
        LoadTestPalette();
        MethodInfo method = AccessTools.Method(typeof(ThemeTests), nameof(ReadColor));
        var ours = new Harmony(TestOwner);
        var other = new Harmony(OtherOwner);
        try
        {
            ours.Patch(method, transpiler: new HarmonyMethod(typeof(ThemePatches), nameof(ThemePatches.Transpiler)) { priority = Priority.Last });
            Check(ReadColor().Equals(Color.black), "The real patched method should receive the theme colour.");
            other.Patch(method, transpiler: new HarmonyMethod(typeof(ThemeTests), nameof(OtherTranspiler)));
            Check(ReadColor().Equals(CustomColor), "An explicit colour supplied by another transpiler should win.");
            ours.Unpatch(method, HarmonyPatchType.Transpiler, TestOwner);
            Check(ReadColor().Equals(CustomColor), "Removing our patch must preserve the other mod's patch.");
            other.Patch(method, prefix: new HarmonyMethod(typeof(ThemeTests), nameof(OtherPrefix)));
            ours.Patch(method, transpiler: new HarmonyMethod(typeof(ThemePatches), nameof(ThemePatches.Transpiler)) { priority = Priority.Last });
            Check(ReadColor().Equals(Color.cyan), "A prefix that takes ownership of drawing should retain control.");
        }
        finally
        {
            ours.Unpatch(method, HarmonyPatchType.All, TestOwner);
            other.Unpatch(method, HarmonyPatchType.All, OtherOwner);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static Color ReadColor() => FloatMenuOption.ColorBGActive;

    private static Color ChangeColor(Color _) => CustomColor;

    private static IEnumerable<CodeInstruction> OtherTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            yield return instruction;
            if (instruction.LoadsField(TestField))
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ThemeTests), nameof(ChangeColor)));
        }
    }

    private static bool OtherPrefix(ref Color __result)
    {
        __result = Color.cyan;
        return false;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Expect<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
