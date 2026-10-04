using Godot;
using System;

namespace Sidereal.Ui;

/// <summary>The shared, editable skin. Gameplay state never lives in this resource.</summary>
[GlobalClass]
public partial class SiderealPalette : Resource
{
    [Export] public Color Surface { get; set; } = new("071b35");
    [Export] public Color Accent { get; set; } = new("00dbff");
    [Export] public Color Text { get; set; } = new("dfedff");
    [Export] public Color Muted { get; set; } = new("83a8ca");
    [Export] public Color Danger { get; set; } = new("ff526b");
    [Export] public Color Success { get; set; } = new("4ce3aa");
    [Export] public Color Warning { get; set; } = new("ffcb54");
    [Export] public Color Rare { get; set; } = new("57a8ff");
    [Export] public Color Epic { get; set; } = new("d771ff");
    [Export(PropertyHint.Range, "0.35,1,0.01")] public float Opacity { get; set; } = 0.91f;
    [Export(PropertyHint.Range, "0.8,1.4,0.05")] public float UiScale { get; set; } = 1;

    public static SiderealPalette Current { get; private set; } = null!;
    public Color Rarity(string value) => value.ToLowerInvariant() switch {
        "uncommon" => Success, "rare" => Rare,
        "epic" => Epic, "legendary" => Warning,
        _ => Muted
    };
    public static SiderealPalette Activate(SiderealPalette palette) => Current = palette;

    public static SiderealPalette LoadProfile()
    {
        Current = ResourceLoader.Load<SiderealPalette>("res://Ui/default_palette.tres") ?? new SiderealPalette();
        var profile = new ConfigFile();
        if (profile.Load("user://ui-theme.cfg") == Error.Ok)
        {
            Current.Surface = ReadColor(profile, "surface", Current.Surface);
            Current.Accent = ReadColor(profile, "accent", Current.Accent);
            Current.Text = ReadColor(profile, "text", Current.Text);
            Current.Muted = ReadColor(profile, "muted", Current.Muted);
            Current.Danger = ReadColor(profile, "danger", Current.Danger);
            Current.Success = ReadColor(profile, "success", Current.Success);
            Current.Warning = ReadColor(profile, "warning", Current.Warning);
            Current.Rare = ReadColor(profile, "rare", Current.Rare);
            Current.Epic = ReadColor(profile, "epic", Current.Epic);
            Current.Opacity = Mathf.Clamp((float)profile.GetValue("theme", "opacity", Current.Opacity).AsDouble(), 0.35f, 1);
            Current.UiScale = Mathf.Clamp((float)profile.GetValue("theme", "scale", Current.UiScale).AsDouble(), 0.8f, 1.4f);
        }
        return Current;
    }

    private static Color ReadColor(ConfigFile file, string key, Color fallback)
    {
        var value = file.GetValue("theme", key, fallback);
        return value.VariantType == Variant.Type.Color ? value.AsColor() : fallback;
    }

    public void ApplyAndSave()
    {
        EmitChanged();
        var file = new ConfigFile();
        file.SetValue("theme", "surface", Surface); file.SetValue("theme", "accent", Accent);
        file.SetValue("theme", "text", Text); file.SetValue("theme", "muted", Muted);
        file.SetValue("theme", "danger", Danger); file.SetValue("theme", "success", Success);
        file.SetValue("theme", "warning", Warning); file.SetValue("theme", "rare", Rare); file.SetValue("theme", "epic", Epic);
        file.SetValue("theme", "opacity", Opacity); file.SetValue("theme", "scale", UiScale);
        file.Save("user://ui-theme.cfg");
    }

    public Theme CreateTheme()
    {
        var theme = new Theme { DefaultFontSize = 17, DefaultFont = ResourceLoader.Load<Font>("res://Ui/Fonts/Barlow-Regular.ttf") };
        var heading = ResourceLoader.Load<Font>("res://Ui/Fonts/BarlowCondensed-SemiBold.ttf");
        theme.SetTypeVariation("Heading", "Label");
        theme.SetTypeVariation("TooltipPanel", "PanelContainer");
        theme.SetTypeVariation("TooltipLabel", "Label");
        theme.SetFont("font", "Heading", heading); theme.SetFontSize("font_size", "Heading", 27);
        theme.SetTypeVariation("AccentButton", "Button");
        foreach (var type in new[] { "Label", "Button", "LineEdit", "OptionButton", "CheckButton", "CheckBox", "TooltipLabel", "RichTextLabel" })
        {
            theme.SetColor("font_color", type, Text);
            theme.SetColor("font_hover_color", type, Colors.White);
            theme.SetColor("font_pressed_color", type, Accent);
            theme.SetColor("font_disabled_color", type, Muted.Darkened(0.35f));
            theme.SetColor("font_focus_color", type, Colors.White);
        }
        theme.SetColor("font_placeholder_color", "LineEdit", Muted);
        theme.SetColor("caret_color", "LineEdit", Accent);
        theme.SetColor("selection_color", "LineEdit", new Color(Accent, 0.3f));
        theme.SetStylebox("panel", "PanelContainer", Box(Surface, Accent.Darkened(0.6f), 14));
        theme.SetStylebox("panel", "TooltipPanel", Box(Surface.Lightened(0.04f), Accent, 14));
        theme.SetFontSize("font_size", "TooltipLabel", 16);
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            theme.SetStylebox("normal", type, Box(Surface, Accent.Darkened(0.45f), 10));
            theme.SetStylebox("hover", type, Box(Surface.Lightened(0.1f), Accent, 10));
            theme.SetStylebox("pressed", type, Box(Surface.Lightened(0.18f), Accent, 10));
            theme.SetStylebox("disabled", type, Box(Surface.Darkened(0.25f), Muted.Darkened(0.6f), 10));
            var focus = Box(new Color(0, 0, 0, 0), Accent, 0); focus.BgColor = Colors.Transparent;
            focus.BorderWidthBottom = focus.BorderWidthTop = focus.BorderWidthLeft = focus.BorderWidthRight = 2;
            theme.SetStylebox("focus", type, focus);
        }
        theme.SetStylebox("normal", "AccentButton", Box(Surface.Lightened(0.14f), Accent, 12));
        theme.SetColor("font_color", "AccentButton", Accent);
        theme.SetStylebox("normal", "LineEdit", Box(Surface.Darkened(0.2f), Muted.Darkened(0.35f), 10));
        theme.SetStylebox("focus", "LineEdit", Box(Surface.Darkened(0.1f), Accent, 10));
        theme.SetStylebox("read_only", "LineEdit", Box(Surface, Muted.Darkened(0.65f), 10));
        theme.SetStylebox("background", "ProgressBar", Box(Surface.Darkened(0.2f), Accent.Darkened(0.6f), 0));
        theme.SetStylebox("fill", "ProgressBar", Box(Accent, Accent, 0));
        theme.SetStylebox("slider", "HSlider", Box(Surface.Darkened(0.25f), Accent.Darkened(0.6f), 0));
        theme.SetStylebox("grabber_area", "HSlider", Box(Accent.Darkened(0.45f), Accent.Darkened(0.2f), 0));
        theme.SetStylebox("grabber_area_highlight", "HSlider", Box(Accent.Darkened(0.2f), Accent, 0));
        var thumb = Image.CreateEmpty(10, 17, false, Image.Format.Rgba8); thumb.Fill(Accent);
        theme.SetIcon("grabber", "HSlider", ImageTexture.CreateFromImage(thumb));
        theme.SetIcon("grabber_highlight", "HSlider", ImageTexture.CreateFromImage(thumb));
        foreach (var scroll in new[] { "VScrollBar", "HScrollBar" })
        {
            var track = Box(Surface.Darkened(0.3f), Surface, 0); track.ContentMarginLeft = track.ContentMarginRight = 5;
            theme.SetStylebox("scroll", scroll, track);
            theme.SetStylebox("grabber", scroll, Box(Accent.Darkened(0.65f), Accent.Darkened(0.65f), 0));
            theme.SetStylebox("grabber_highlight", scroll, Box(Accent.Darkened(0.2f), Accent, 0));
            theme.SetStylebox("grabber_pressed", scroll, Box(Accent, Accent, 0));
        }
        theme.SetStylebox("panel", "PopupMenu", Box(Surface, Accent.Darkened(0.4f), 8));
        theme.SetStylebox("hover", "PopupMenu", Box(Surface.Lightened(0.15f), Accent.Darkened(0.3f), 5));
        theme.SetColor("font_color", "PopupMenu", Text); theme.SetColor("font_hover_color", "PopupMenu", Colors.White);
        theme.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = Accent.Darkened(0.65f), Thickness = 1, GrowBegin = 0, GrowEnd = 0 });
        theme.SetConstant("separation", "VBoxContainer", 10);
        theme.SetConstant("separation", "HBoxContainer", 10);
        theme.SetConstant("h_separation", "GridContainer", 10);
        theme.SetConstant("v_separation", "GridContainer", 10);
        return theme;
    }

    private StyleBoxFlat Box(Color fill, Color line, int padding) => new() {
        BgColor = new Color(fill, Opacity), BorderColor = line,
        BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
        CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3, CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3,
        ContentMarginLeft = padding, ContentMarginRight = padding, ContentMarginTop = padding, ContentMarginBottom = padding
    };
}
