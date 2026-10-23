using System.Reflection;
using UnityEngine;
using Verse;

namespace FlatBlack;

internal sealed class ThemePreview
{
    private enum Section { Buttons, Checkboxes, Radios, Options, Text }
    private enum ButtonState { Normal, Hover, Pressed, Disabled }
    private enum TextState { Healthy, Warning, Disabled }

    private const float Padding = 12f;
    private const float PanelPadding = 10f;
    private static float HeaderHeight => Text.LineHeightOf(GameFont.Medium);
    private const float Gap = 8f;
    private const float MinimumCellWidth = 150f;
    private const float SelectedOptionBorderOutset = 3f;
    private const float IndicatorSize = 24f;
    private const float IndicatorLabelGap = 6f;
    private static readonly ButtonState[] ButtonStates = Enum.GetValues<ButtonState>();
    private static readonly TextState[] TextStates = Enum.GetValues<TextState>();
    private static readonly MultiCheckboxState[] CheckboxStates = [MultiCheckboxState.On, MultiCheckboxState.Off, MultiCheckboxState.Partial];
    private static readonly bool[] SelectionStates = [true, false];
    private static readonly FieldInfo HoverTexture = TextureField("ButtonBGAtlasMouseover");
    private static readonly FieldInfo NormalTexture = TextureField(nameof(Widgets.ButtonBGAtlas));
    private static readonly FieldInfo PressedTexture = TextureField(nameof(Widgets.ButtonBGAtlasClick));
    private static readonly FieldInfo PartialTexture = TextureField(nameof(Widgets.CheckboxPartialTex));
    private static readonly (Section Section, int Count, int MaximumColumns, float CellHeight)[] Sections =
    [
        (Section.Buttons, ButtonStates.Length, ButtonStates.Length, 32f),
        (Section.Checkboxes, CheckboxStates.Length, CheckboxStates.Length, 28f),
        (Section.Radios, SelectionStates.Length, SelectionStates.Length, 28f),
        (Section.Options, SelectionStates.Length, SelectionStates.Length, 32f),
        (Section.Text, TextStates.Length, TextStates.Length, 26f)
    ];
    private Vector2 scroll;

    private readonly record struct Grid(float Width, int Count, int Columns, float CellHeight)
    {
        private int Rows => (Count + Columns - 1) / Columns;
        internal float Height => Rows * CellHeight + (Rows - 1) * Gap;

        internal Rect Cell(int index, float y)
        {
            float width = (Width - (Columns - 1) * Gap) / Columns;
            return new Rect(index % Columns * (width + Gap), y + index / Columns * (CellHeight + Gap), width, CellHeight);
        }
    }

    internal static (Rect Controls, Rect Preview) Arrange(Rect area)
    {
        float contentWidth = ContentWidth(area.width);
        float fullHeight = Padding * 2 + HeaderHeight + Gap + PanelPadding * 2 + ContentHeight(contentWidth);
        float previewHeight = Math.Min(fullHeight, area.height / 2f);
        return (
            new Rect(area.x, area.y, area.width, Math.Max(0f, area.height - previewHeight - Gap)),
            new Rect(area.x, area.yMax - previewHeight, area.width, previewHeight)
        );
    }

    internal void Draw(Rect rect)
    {
        Widgets.DrawWindowBackground(rect);
        Rect inner = rect.ContractedBy(Padding);
        GameFont previousFont = Text.Font;
        try
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inner.x, inner.y, inner.width, HeaderHeight), "FlatBlack_Preview".Translate());
        }
        finally
        {
            Text.Font = previousFont;
        }
        Rect viewport = new(inner.x, inner.y + HeaderHeight + Gap, inner.width, Math.Max(0f, inner.height - HeaderHeight - Gap));
        float width = ContentWidth(rect.width);
        float contentHeight = ContentHeight(width);
        Widgets.BeginScrollView(viewport, ref scroll, new Rect(0, 0, width + PanelPadding * 2, contentHeight + PanelPadding * 2));
        try
        {
            Widgets.DrawMenuSection(new Rect(0, 0, width + PanelPadding * 2, contentHeight + PanelPadding * 2));
            GUI.BeginGroup(new Rect(PanelPadding, PanelPadding, width, contentHeight));
            try
            {
                float y = 0f;
                foreach ((Section section, int count, int maximumColumns, float cellHeight) in Sections)
                {
                    Grid grid = CreateGrid(width, count, maximumColumns, cellHeight);
                    for (int index = 0; index < count; index++)
                        DrawSample(section, index, grid.Cell(index, y));
                    y += grid.Height + Gap;
                }
            }
            finally
            {
                GUI.EndGroup();
            }
        }
        finally
        {
            Widgets.EndScrollView();
        }
    }

    private static float ContentWidth(float width) => Math.Max(1f, width - Padding * 2 - PanelPadding * 2 - 20f);

    private static Grid CreateGrid(float width, int count, int maximumColumns, float cellHeight) =>
        new(width, count, Math.Clamp((int)((width + Gap) / (MinimumCellWidth + Gap)), 1, maximumColumns), cellHeight);

    private static float ContentHeight(float width)
    {
        float height = 0f;
        foreach ((_, int count, int maximumColumns, float cellHeight) in Sections)
            height += CreateGrid(width, count, maximumColumns, cellHeight).Height + Gap;
        return height - Gap;
    }

    private static void DrawSample(Section section, int index, Rect rect)
    {
        switch (section)
        {
            case Section.Buttons:
                DrawButton(rect, ButtonStates[index]);
                break;
            case Section.Checkboxes:
                DrawCheckbox(rect, CheckboxStates[index]);
                break;
            case Section.Radios:
                bool selected = SelectionStates[index];
                Rect radio = IndicatorRect(rect);
                Widgets.RadioButton(radio.position, selected);
                DrawIndicatorLabel(rect, selected ? "FlatBlack_RadioSelected" : "FlatBlack_RadioUnselected");
                break;
            case Section.Options:
                bool active = SelectionStates[index];
                // Keep RimWorld's outer selection border inside the preview cell's clip area.
                Rect option = active ? rect.ContractedBy(SelectedOptionBorderOutset) : rect;
                Widgets.DrawOptionBackground(option, active);
                Widgets.Label(rect.ContractedBy(4), (active ? "FlatBlack_Selected" : "FlatBlack_Unselected").Translate());
                break;
            case Section.Text:
                DrawText(rect, TextStates[index]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(section));
        }
    }

    private static void DrawButton(Rect rect, ButtonState state)
    {
        string label = state switch
        {
            ButtonState.Normal => "FlatBlack_Button",
            ButtonState.Hover => "FlatBlack_Hover",
            ButtonState.Pressed => "FlatBlack_Pressed",
            ButtonState.Disabled => "FlatBlack_Disabled",
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        if (state == ButtonState.Normal)
        {
            Widgets.ButtonText(rect, label.Translate());
            return;
        }

        FieldInfo texture = state switch
        {
            ButtonState.Hover => HoverTexture,
            ButtonState.Pressed => PressedTexture,
            ButtonState.Disabled => NormalTexture,
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        TextAnchor previousAnchor = Text.Anchor;
        Color previousColor = GUI.color;
        try
        {
            if (state == ButtonState.Disabled)
                GUI.color = Widgets.InactiveColor;
            Widgets.DrawAtlas(rect, ThemeResources.ResolveTextureField(texture));
            Text.Anchor = TextAnchor.MiddleCenter;
            if (state == ButtonState.Disabled)
                GUI.color = FloatMenuOption.ColorTextDisabled;
            Widgets.Label(rect, label.Translate());
        }
        finally
        {
            Text.Anchor = previousAnchor;
            GUI.color = previousColor;
        }
    }

    private static void DrawCheckbox(Rect rect, MultiCheckboxState state)
    {
        string label = state switch
        {
            MultiCheckboxState.On => "FlatBlack_Checked",
            MultiCheckboxState.Off => "FlatBlack_Unchecked",
            MultiCheckboxState.Partial => "FlatBlack_Partial",
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        Rect icon = IndicatorRect(rect);
        if (state == MultiCheckboxState.Partial)
            GUI.DrawTexture(icon, ThemeResources.ResolveTextureField(PartialTexture));
        else
            Widgets.CheckboxDraw(icon.x, icon.y, state == MultiCheckboxState.On, disabled: false, size: icon.width);
        DrawIndicatorLabel(rect, label);
    }

    private static Rect IndicatorRect(Rect rect) =>
        new(rect.x, rect.y + (rect.height - IndicatorSize) / 2f, IndicatorSize, IndicatorSize);

    private static void DrawIndicatorLabel(Rect rect, string label)
    {
        float offset = IndicatorSize + IndicatorLabelGap;
        Widgets.Label(new Rect(rect.x + offset, rect.y, rect.width - offset, rect.height), label.Translate());
    }

    private static void DrawText(Rect rect, TextState state)
    {
        (string label, Color color) = state switch
        {
            TextState.Healthy => ("FlatBlack_Healthy", Color.green),
            TextState.Warning => ("FlatBlack_Warning", Color.red),
            TextState.Disabled => ("FlatBlack_DisabledText", FloatMenuOption.ColorTextDisabled),
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        Color previousColor = GUI.color;
        try
        {
            GUI.color = color;
            Widgets.Label(rect, label.Translate());
        }
        finally
        {
            GUI.color = previousColor;
        }
    }

    private static FieldInfo TextureField(string name) =>
        typeof(Widgets).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException(typeof(Widgets).FullName, name);
}
