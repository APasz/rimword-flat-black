using System.Collections.Frozen;
using System.Reflection;
using UnityEngine;

namespace FlatBlack;

internal static class ThemeResources
{
    private readonly record struct Replacement<T>(T Original, T Themed);
    private readonly record struct ColorBinding(Color Original, ColorOverride Definition);
    private readonly record struct TextureBinding(Texture2D Original, TextureOverride Definition, TextureVariant Variant, Texture2D? Override);

    private static ThemeDef? definition;
    private static FrozenDictionary<RuntimeFieldHandle, ColorBinding> colorBindings = FrozenDictionary<RuntimeFieldHandle, ColorBinding>.Empty;
    private static FrozenDictionary<RuntimeFieldHandle, TextureBinding> textureBindings = FrozenDictionary<RuntimeFieldHandle, TextureBinding>.Empty;
    private static FrozenDictionary<RuntimeFieldHandle, Replacement<Color>> colors = FrozenDictionary<RuntimeFieldHandle, Replacement<Color>>.Empty;
    private static FrozenDictionary<RuntimeFieldHandle, Replacement<Texture2D>> textures = FrozenDictionary<RuntimeFieldHandle, Replacement<Texture2D>>.Empty;
    private static TextureVariant[] variants = [];

    internal static IReadOnlySet<FieldInfo> Load(ThemeDef theme, Func<string, Texture2D> loadTexture,
        Func<TextureOverride, Texture2D, Texture2D?>? findOverride = null)
    {
        theme.Validate();
        var fields = new HashSet<FieldInfo>();
        var loadedColors = new Dictionary<RuntimeFieldHandle, ColorBinding>();
        var loadedTextures = new Dictionary<RuntimeFieldHandle, TextureBinding>();
        var loadedVariants = new Dictionary<(Texture2D Source, OpacityChannel Opacity, bool Recolor, bool Mipmaps), TextureVariant>();

        foreach (ColorOverride entry in theme.colors)
        {
            FieldInfo field = entry.ResolveField();
            Color original = (Color)field.GetValue(null)!;
            loadedColors.Add(field.FieldHandle, new ColorBinding(original, entry));
            fields.Add(field);
        }
        var nextColorBindings = loadedColors.ToFrozenDictionary();
        var nextColors = loadedColors.ToFrozenDictionary(pair => pair.Key,
            pair => new Replacement<Color>(pair.Value.Original, pair.Value.Definition.value));
        FrozenDictionary<RuntimeFieldHandle, TextureBinding> nextTextureBindings;
        FrozenDictionary<RuntimeFieldHandle, Replacement<Texture2D>> nextTextures;

        try
        {
            foreach (TextureOverride entry in theme.textures)
            {
                FieldInfo field = entry.ResolveField();
                Texture2D original = field.GetValue(null) as Texture2D
                    ?? throw new InvalidOperationException($"UI texture is not initialized: {entry.declaringType.FullName}.{entry.fieldName}.");
                Texture2D source = loadTexture(entry.texturePath);
                var key = (source, entry.opacity, entry.recolor, entry.mipmaps);
                if (!loadedVariants.TryGetValue(key, out TextureVariant? variant))
                {
                    variant = new TextureVariant(source, entry.opacity, entry.recolor, entry.mipmaps);
                    loadedVariants.Add(key, variant);
                }
                loadedTextures.Add(field.FieldHandle, new TextureBinding(original, entry, variant, findOverride?.Invoke(entry, original)));
                fields.Add(field);
            }
            nextTextureBindings = loadedTextures.ToFrozenDictionary();
            nextTextures = loadedTextures.ToFrozenDictionary(pair => pair.Key,
                pair => new Replacement<Texture2D>(pair.Value.Original, pair.Value.Override ?? pair.Value.Variant.Source));
        }
        catch
        {
            foreach (TextureVariant variant in loadedVariants.Values)
                variant.Dispose();
            throw;
        }

        foreach (TextureVariant variant in variants)
            variant.Dispose();
        definition = theme;
        variants = loadedVariants.Values.ToArray();
        colorBindings = nextColorBindings;
        textureBindings = nextTextureBindings;
        colors = nextColors;
        textures = nextTextures;
        return fields;
    }

    internal static void Apply(ThemeOptions options)
    {
        options.Validate();
        ThemeDef theme = definition ?? throw new InvalidOperationException("Theme resources have not been loaded.");
        var updatedColors = new Dictionary<RuntimeFieldHandle, Replacement<Color>>(colorBindings.Count);
        var updatedTextures = new Dictionary<RuntimeFieldHandle, Replacement<Texture2D>>(textureBindings.Count);
        foreach ((RuntimeFieldHandle field, ColorBinding binding) in colorBindings)
        {
            Color color = binding.Original;
            if (options.IsEnabled(binding.Definition.component))
            {
                color = options.Recolor(binding.Definition.value);
                color.a = Math.Clamp(color.a * options.OpacityScale(binding.Definition.opacity, theme), 0f, 1f);
            }
            updatedColors.Add(field, new Replacement<Color>(binding.Original, color));
        }
        foreach ((RuntimeFieldHandle field, TextureBinding binding) in textureBindings)
        {
            Texture2D texture = binding.Override ?? (options.IsEnabled(binding.Definition.component)
                ? binding.Variant.Resolve(options, theme) : binding.Original);
            updatedTextures.Add(field, new Replacement<Texture2D>(binding.Original, texture));
        }
        colors = updatedColors.ToFrozenDictionary();
        textures = updatedTextures.ToFrozenDictionary();
    }

    internal static bool IsColor(FieldInfo field) => colors.ContainsKey(field.FieldHandle);
    internal static bool IsTexture(FieldInfo field) => textures.ContainsKey(field.FieldHandle);

    // Preserve later changes made by other mods without mutating shared game resources.
    public static Color ResolveColor(Color current, RuntimeFieldHandle field)
    {
        Replacement<Color> replacement = colors[field];
        return current.Equals(replacement.Original) ? replacement.Themed : current;
    }

    public static Texture2D ResolveTexture(Texture2D current, RuntimeFieldHandle field)
    {
        Replacement<Texture2D> replacement = textures[field];
        return ReferenceEquals(current, replacement.Original) ? replacement.Themed : current;
    }

    internal static Texture2D ResolveTextureField(FieldInfo field) => ResolveTexture(
        field.GetValue(null) as Texture2D ?? throw new InvalidOperationException($"UI texture is not initialized: {field.DeclaringType!.FullName}.{field.Name}."),
        field.FieldHandle);

    internal static Color[] ReadTexturePixels(Texture2D source)
    {
        // The Unity-compatible bulk reader drops packed texture channel mappings.
        Waylith.Math.Color[] nativePixels = source.CoreTexture.GetPixels();
        var pixels = new Color[nativePixels.Length];
        int width = source.width;
        int height = source.height;
        for (int row = 0; row < height; row++)
        for (int column = 0; column < width; column++)
        {
            Waylith.Math.Color pixel = nativePixels[(height - row - 1) * width + column];
            pixels[row * width + column] = new Color(pixel.r, pixel.g, pixel.b, pixel.a);
        }
        return pixels;
    }

    private sealed class TextureVariant(Texture2D source, OpacityChannel opacity, bool recolor, bool mipmaps) : IDisposable
    {
        private readonly Texture2D original = source;
        internal Texture2D Source => prepared ??= PrepareSource(original, mipmaps);
        private Texture2D? prepared;
        private Texture2D current = source;
        private Texture2D? customized;
        private Color[]? originalPixels;
        private Color[]? workingPixels;
        private (float Scale, bool Recolor, Color Primary, Color Secondary)? applied;

        internal Texture2D Resolve(ThemeOptions options, ThemeDef theme)
        {
            float scale = options.OpacityScale(opacity, theme);
            bool changeAccents = recolor && options.CustomAccents;
            if (scale == 1f && !changeAccents)
                return Source;

            var key = (scale, changeAccents, changeAccents ? options.PrimaryAccent : Color.clear,
                changeAccents ? options.SecondaryAccent : Color.clear);
            if (applied == key)
                return current;

            originalPixels ??= ReadTexturePixels(Source);
            workingPixels ??= new Color[originalPixels.Length];
            bool changed = false;
            for (int index = 0; index < originalPixels.Length; index++)
            {
                Color pixel = changeAccents ? options.Recolor(originalPixels[index]) : originalPixels[index];
                pixel.a = Math.Clamp(pixel.a * scale, 0f, 1f);
                workingPixels[index] = pixel;
                changed |= !pixel.Equals(originalPixels[index]);
            }

            current = Source;
            if (changed)
            {
                customized ??= CreateTexture(Source, $"{Source.name} (Flat Black settings)", Source.mipmapCount > 1);
                customized.SetPixels(workingPixels);
                customized.Apply(updateMipmaps: Source.mipmapCount > 1, makeNoLongerReadable: false);
                current = customized;
            }
            applied = key;
            return current;
        }

        private static Texture2D PrepareSource(Texture2D source, bool mipmaps)
        {
            if (mipmaps || source.mipmapCount <= 1)
                return source;

            // Mipmaps mix atlas borders into narrow centre slices on short controls.
            Color[] pixels = ReadTexturePixels(source);
            Texture2D texture = CreateTexture(source, $"{source.name} (Flat Black atlas)", mipmaps: false);
            try
            {
                texture.SetPixels(pixels);
                texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                return texture;
            }
            catch
            {
                UnityEngine.Object.Destroy(texture);
                throw;
            }
        }

        private static Texture2D CreateTexture(Texture2D source, string name, bool mipmaps) =>
            new(source.width, source.height, TextureFormat.RGBA32,
                mipCount: mipmaps ? source.mipmapCount : 1, linear: !source.isDataSRGB, createUninitialized: true)
            {
                name = name,
                filterMode = source.filterMode,
                wrapMode = source.wrapMode,
                anisoLevel = source.anisoLevel,
                mipMapBias = source.mipMapBias
            };

        public void Dispose()
        {
            if (customized is not null)
                UnityEngine.Object.Destroy(customized);
            if (prepared is not null && !ReferenceEquals(prepared, original))
                UnityEngine.Object.Destroy(prepared);
        }
    }
}
