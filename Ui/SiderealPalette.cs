using Godot;
using System;

namespace Sidereal.Ui;

/// <summary>The shared, editable skin. Gameplay state never lives in this resource.</summary>
[GlobalClass]
public partial class SiderealPalette : Resource
{
    [Export] public Color Surface { get; set; } = new("051736");
    [Export] public Color Accent { get; set; } = new("47dfff");
    [Export] public Color Text { get; set; } = new("eff6ff");
    [Export] public Color Muted { get; set; } = new("a7c5e8");
    [Export] public Color Danger { get; set; } = new("ff8eaa");
    [Export] public Color Success { get; set; } = new("74dcbb");
    [Export] public Color Warning { get; set; } = new("ffd26d");
    [Export] public Color Common { get; set; } = new("9db5d0");
    [Export] public Color Uncommon { get; set; } = new("33e894");
    [Export] public Color Rare { get; set; } = new("27cfff");
    [Export] public Color Epic { get; set; } = new("c065ff");
    [Export] public Color Legendary { get; set; } = new("ffcc42");
    [Export(PropertyHint.Range, "0.3,1,0.01")] public float Opacity { get; set; } = 0.94f;
    [Export(PropertyHint.Range, "0.75,1.5,0.05")] public float UiScale { get; set; } = 1;

    public static SiderealPalette Current { get; private set; } = null!;
    public string? PersistenceError { get; private set; }
    public Color Rarity(string value) => value.ToLowerInvariant() switch {
        "uncommon" => Uncommon, "rare" => Rare,
        "epic" => Epic, "legendary" => Legendary,
        _ => Common
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
            Current.Common = ReadColor(profile, "common", Current.Common);
            Current.Uncommon = ReadColor(profile, "uncommon", Current.Uncommon);
            Current.Legendary = ReadColor(profile, "legendary", Current.Legendary);
            Current.Opacity = ReadRange(profile,"opacity",Current.Opacity,.3f,1);
            Current.UiScale = ReadRange(profile,"scale",Current.UiScale,.75f,1.5f);
        }
        return Current;
    }

    private static Color ReadColor(ConfigFile file, string key, Color fallback)
    {
        var value = file.GetValue("theme", key, fallback);
        if(value.VariantType!=Variant.Type.Color)return fallback;var color=value.AsColor();
        return float.IsFinite(color.R)&&float.IsFinite(color.G)&&float.IsFinite(color.B)&&float.IsFinite(color.A)?color:fallback;
    }
    private static float ReadRange(ConfigFile file,string key,float fallback,float minimum,float maximum)
    {
        var value=file.GetValue("theme",key,fallback);if(value.VariantType is not (Variant.Type.Int or Variant.Type.Float))return fallback;
        var number=value.AsDouble();return double.IsFinite(number)?(float)Math.Clamp(number,minimum,maximum):fallback;
    }

    public void ApplyAndSave()
    {
        EmitChanged();
        var file = new ConfigFile();
        file.SetValue("theme", "surface", Surface); file.SetValue("theme", "accent", Accent);
        file.SetValue("theme", "text", Text); file.SetValue("theme", "muted", Muted);
        file.SetValue("theme", "danger", Danger); file.SetValue("theme", "success", Success);
        file.SetValue("theme", "warning", Warning); file.SetValue("theme", "rare", Rare); file.SetValue("theme", "epic", Epic);
        file.SetValue("theme", "common", Common); file.SetValue("theme", "uncommon", Uncommon); file.SetValue("theme", "legendary", Legendary);
        file.SetValue("theme", "opacity", Opacity); file.SetValue("theme", "scale", UiScale);
        PersistenceError=file.Save("user://ui-theme.cfg")==Error.Ok?null:"Theme changes apply for this session but could not be saved.";
    }

    public Theme CreateTheme()
    {
        var theme = new Theme { DefaultFontSize = 17, DefaultFont = ResourceLoader.Load<Font>("res://Ui/Fonts/Barlow-Regular.ttf") };
        var heading = ResourceLoader.Load<Font>("res://Ui/Fonts/BarlowCondensed-SemiBold.ttf");
        theme.SetTypeVariation("Heading", "Label");
        theme.SetTypeVariation("AccentHeading", "Heading");
        theme.SetTypeVariation("AccentLabel", "Label");
        theme.SetTypeVariation("MutedLabel", "Label");
        theme.SetTypeVariation("WarningLabel", "Label");
        theme.SetTypeVariation("DangerHeading", "Heading");
        theme.SetTypeVariation("FramePanel", "PanelContainer");
        theme.SetTypeVariation("CompactFramePanel", "PanelContainer");
        theme.SetTypeVariation("ActionBarPanel", "PanelContainer");
        theme.SetTypeVariation("QuietPanel", "PanelContainer");
        theme.SetTypeVariation("TooltipPanel", "PanelContainer");
        theme.SetTypeVariation("TooltipLabel", "Label");
        theme.SetFont("font", "Heading", heading); theme.SetFontSize("font_size", "Heading", 27);
        theme.SetTypeVariation("AccentButton", "Button");
        theme.SetTypeVariation("GhostButton", "Button");
        theme.SetTypeVariation("SelectedMenuTab", "Button");
        theme.SetTypeVariation("CompactOption", "OptionButton");
        theme.SetTypeVariation("CompactInput", "LineEdit");
        foreach (var type in new[] { "Label", "Button", "LineEdit", "OptionButton", "CheckButton", "CheckBox", "TooltipLabel", "RichTextLabel" })
        {
            theme.SetColor("font_color", type, Text);
            theme.SetColor("font_hover_color", type, Colors.White);
            theme.SetColor("font_pressed_color", type, Accent);
            theme.SetColor("font_disabled_color", type, Muted.Darkened(0.35f));
            theme.SetColor("font_focus_color", type, Colors.White);
        }
        theme.SetColor("font_placeholder_color", "LineEdit", Muted);
        theme.SetColor("font_color", "AccentHeading", Accent);
        theme.SetColor("font_color", "AccentLabel", Accent);
        theme.SetColor("font_color", "MutedLabel", Muted);
        theme.SetColor("font_color", "WarningLabel", Warning);
        theme.SetColor("font_color", "DangerHeading", Danger);
        theme.SetColor("caret_color", "LineEdit", Accent);
        theme.SetColor("selection_color", "LineEdit", new Color(Accent, 0.3f));
        theme.SetStylebox("panel", "PanelContainer", Frame(Surface, new Color("294c68"), 14, 9, false));
        theme.SetStylebox("panel", "FramePanel", Frame(Surface, Accent.Darkened(.45f), 16, 12, true));
        theme.SetStylebox("panel", "CompactFramePanel", Frame(Surface, Accent.Darkened(.45f), 8, 10, true));
        theme.SetStylebox("panel", "ActionBarPanel", new StyleBoxEmpty());
        theme.SetStylebox("panel", "QuietPanel", Frame(Surface, Accent.Darkened(.65f), 10, 7, false));
        theme.SetStylebox("panel", "TooltipPanel", Frame(Surface.Lightened(0.04f), Accent, 14, 10, true));
        theme.SetFontSize("font_size", "TooltipLabel", 16);
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            theme.SetStylebox("normal", type, Frame(Surface.Lightened(.02f), Accent.Darkened(0.52f), 12, 7, false));
            theme.SetStylebox("hover", type, Frame(Surface.Lightened(0.09f), Accent, 12, 7, false));
            theme.SetStylebox("pressed", type, Frame(Surface.Lightened(0.16f), Accent, 12, 7, true));
            theme.SetStylebox("disabled", type, Frame(Surface.Darkened(0.25f), Muted.Darkened(0.7f), 12, 7, false));
            var focus = Frame(Colors.Transparent, Accent, 0, 7, false); focus.BorderWidth = 2;
            theme.SetStylebox("focus", type, focus);
        }
        theme.SetFont("font", "AccentButton", heading); theme.SetFontSize("font_size", "AccentButton", 24);
        theme.SetStylebox("normal", "AccentButton", Frame(Surface.Lightened(0.08f), Accent, 16, 10, true));
        theme.SetColor("font_color", "AccentButton", Text);
        theme.SetStylebox("normal", "GhostButton", Frame(new Color(Surface, .65f), Accent.Darkened(.65f), 12, 6, false));
        theme.SetFont("font", "SelectedMenuTab", heading);theme.SetFontSize("font_size", "SelectedMenuTab",17);
        theme.SetStylebox("normal", "SelectedMenuTab", Frame(Surface.Lightened(.08f), Accent, 12, 7, true));
        theme.SetStylebox("normal", "LineEdit", Frame(Surface.Darkened(0.2f), Muted.Darkened(0.55f), 10, 6, false));
        theme.SetStylebox("focus", "LineEdit", Frame(Surface.Darkened(0.1f), Accent, 10, 6, false));
        theme.SetStylebox("read_only", "LineEdit", Frame(Surface, Muted.Darkened(0.7f), 10, 6, false));
        theme.SetStylebox("normal", "CompactOption", Frame(Surface.Lightened(.02f), Accent.Darkened(.52f), 6, 6, false));
        theme.SetStylebox("hover", "CompactOption", Frame(Surface.Lightened(.09f), Accent, 6, 6, false));
        theme.SetStylebox("pressed", "CompactOption", Frame(Surface.Lightened(.16f), Accent, 6, 6, true));
        theme.SetStylebox("normal", "CompactInput", Frame(Surface.Darkened(.2f), Muted.Darkened(.55f), 6, 6, false));
        theme.SetStylebox("focus", "CompactInput", Frame(Surface.Darkened(.1f), Accent, 6, 6, false));
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
        theme.SetStylebox("panel", "PopupMenu", Frame(Surface, Accent.Darkened(0.4f), 8, 7, false));
        theme.SetStylebox("hover", "PopupMenu", Box(Surface.Lightened(0.15f), Accent.Darkened(0.3f), 5));
        theme.SetColor("font_color", "PopupMenu", Text); theme.SetColor("font_hover_color", "PopupMenu", Colors.White);
        theme.SetColor("font_disabled_color", "PopupMenu", Muted.Darkened(.35f));
        theme.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = Accent.Darkened(0.65f), Thickness = 1, GrowBegin = 0, GrowEnd = 0 });
        theme.SetConstant("separation", "VBoxContainer", 10);
        theme.SetConstant("separation", "HBoxContainer", 10);
        theme.SetConstant("line_spacing", "Label", 4);
        theme.SetConstant("h_separation", "GridContainer", 10);
        theme.SetConstant("v_separation", "GridContainer", 10);
        return theme;
    }

    private SciFiFrameStyle Frame(Color fill, Color line, int padding, float corner, bool rails)
        => new(new Color(fill, fill.A * Opacity), line, Accent, padding, corner, rails);

    private StyleBoxFlat Box(Color fill, Color line, int padding) => new() {
        BgColor = new Color(fill, Opacity), BorderColor = line,
        BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
        CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3, CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3,
        ContentMarginLeft = padding, ContentMarginRight = padding, ContentMarginTop = padding, ContentMarginBottom = padding
    };
}
