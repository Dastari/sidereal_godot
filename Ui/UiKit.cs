using Godot;
using System;

namespace Sidereal.Ui;

public static class UiKit
{
    public static Label Label(string text, int size = 17)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size); return label;
    }

    public static Label Heading(string text, int size = 27)
    {
        var label = Label(text, size); label.ThemeTypeVariation = "Heading"; return label;
    }

    public static Label Paragraph(string text, int width = 320)
        => new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(width, 0), MouseFilter = Control.MouseFilterEnum.Ignore };

    public static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(0, 42), MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        button.Pressed += () => { action(); button.ReleaseFocus(); }; return button;
    }

    public static PanelContainer Panel(Control content, float padding = 14)
    {
        var panel = new PanelContainer();
        var inset = new MarginContainer();
        foreach (var edge in new[] { "left", "right", "top", "bottom" }) inset.AddThemeConstantOverride("margin_" + edge, (int)padding);
        panel.AddChild(inset); inset.AddChild(content); return panel;
    }

    public static Control Spacer() => new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };

    public static Control Tooltip(string title, string description)
    {
        var column = new VBoxContainer();
        column.AddChild(Heading(title, 23)); column.AddChild(Paragraph(description));
        var panel = new PanelContainer { Theme = SiderealPalette.Current.CreateTheme(), ThemeTypeVariation = "TooltipPanel" };
        panel.AddChild(column); return panel;
    }
}
