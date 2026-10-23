using System.Reflection;
using UnityEngine;
using Verse;

namespace FlatBlack;

public enum ThemeComponent { Unspecified, Backgrounds, Icons, Highlights }
public enum OpacityChannel { None, Window, Panel }

/// <summary>Default appearance and resource bindings shared by the runtime and mod settings.</summary>
public sealed class ThemeDef : Def
{
    public List<TextureOverride> textures = [];
    public List<ColorOverride> colors = [];
    public Color primaryAccent;
    public Color secondaryAccent;

    internal float DefaultOpacity(OpacityChannel channel) => channel switch
    {
        OpacityChannel.None => 1f,
        OpacityChannel.Window => DefaultColor(nameof(Widgets.WindowBGFillColor)).a,
        OpacityChannel.Panel => DefaultColor(nameof(Widgets.MenuSectionBGFillColor)).a,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    private Color DefaultColor(string fieldName) => colors.SingleOrDefault(entry => entry.declaringType == typeof(Widgets) && entry.fieldName == fieldName)?.value
        ?? throw new InvalidOperationException($"Missing default opacity binding for Widgets.{fieldName}.");

    public override IEnumerable<string> ConfigErrors()
    {
        foreach (string error in base.ConfigErrors())
            yield return error;

        var fields = new HashSet<FieldInfo>();
        foreach (UiFieldOverride entry in textures.Cast<UiFieldOverride>().Concat(colors))
        {
            string? error = entry.ValidationError();
            if (error is not null)
                yield return error;
            else if (!fields.Add(entry.ResolveField()))
                yield return $"Duplicate UI field: {entry.declaringType.FullName}.{entry.fieldName}.";
        }
    }

    internal void Validate()
    {
        string[] errors = ConfigErrors().ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException($"Invalid theme {defName}: {string.Join(" ", errors)}");
    }

    internal ThemeOptions DefaultOptions() => new()
    {
        FontPreset = ThemeFontPreset.OxaniumExo2,
        WindowOpacity = DefaultOpacity(OpacityChannel.Window),
        PanelOpacity = DefaultOpacity(OpacityChannel.Panel),
        Backgrounds = true,
        Icons = true,
        Highlights = true,
        PrimaryAccent = primaryAccent,
        SecondaryAccent = secondaryAccent
    };
}

public abstract class UiFieldOverride
{
    public Type declaringType = null!;
    public string fieldName = string.Empty;
    public ThemeComponent component;
    public OpacityChannel opacity;

    protected abstract Type ValueType { get; }

    internal FieldInfo ResolveField()
    {
        if (declaringType is null || declaringType.Assembly != typeof(Widgets).Assembly)
            throw new InvalidOperationException("UI fields must belong to RimWorld's game assembly.");
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new InvalidOperationException($"Missing UI field name for {declaringType.FullName}.");

        FieldInfo field = declaringType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new InvalidOperationException($"UI field no longer exists: {declaringType.FullName}.{fieldName}.");
        if (field.FieldType != ValueType || field.IsLiteral)
            throw new InvalidOperationException($"UI field {declaringType.FullName}.{fieldName} must be a static {ValueType.Name} field.");
        return field;
    }

    internal virtual string? ValidationError()
    {
        try
        {
            ResolveField();
            if (component == ThemeComponent.Unspecified || !Enum.IsDefined(component))
                return $"Missing or invalid theme component for {declaringType.FullName}.{fieldName}.";
            if (!Enum.IsDefined(opacity))
                return $"Invalid opacity channel for {declaringType.FullName}.{fieldName}.";
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }
    }
}

public sealed class TextureOverride : UiFieldOverride
{
    public string texturePath = string.Empty;
    public string vanillaPath = string.Empty;
    public bool recolor;
    public bool mipmaps = true;

    protected override Type ValueType => typeof(Texture2D);

    internal override string? ValidationError()
    {
        string? error = base.ValidationError();
        if (error is not null)
            return error;
        if (!IsContentPath(texturePath))
            return $"Invalid content path for {declaringType.FullName}.{fieldName}: '{texturePath}'. Use a relative path without an extension.";
        if (!texturePath.StartsWith("FlatBlack/", StringComparison.Ordinal))
            return $"Flat Black textures must use the FlatBlack content namespace: {texturePath}.";
        if (vanillaPath.Length > 0 && !IsContentPath(vanillaPath))
            return $"Invalid vanilla content path for {declaringType.FullName}.{fieldName}: '{vanillaPath}'.";
        return null;
    }

    private static bool IsContentPath(string path) => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path)
        && !path.Contains('\\') && !path.Split('/').Any(part => part is "" or "." or "..") && !Path.HasExtension(path);
}

public sealed class ColorOverride : UiFieldOverride
{
    public Color value = new(float.NaN, float.NaN, float.NaN, float.NaN);

    protected override Type ValueType => typeof(Color);

    internal override string? ValidationError()
    {
        string? error = base.ValidationError();
        if (error is not null)
            return error;
        for (int component = 0; component < 4; component++)
        {
            if (!float.IsFinite(value[component]) || value[component] is < 0f or > 1f)
                return $"Invalid colour for {declaringType.FullName}.{fieldName}: each RGBA component must be between 0 and 1.";
        }
        return null;
    }
}
