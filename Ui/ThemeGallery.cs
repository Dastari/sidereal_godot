using Godot;

namespace Sidereal.Ui;

/// <summary>Run Ui/ThemeGallery.tscn with F6 to inspect the shared native widgets without a server.</summary>
public partial class ThemeGallery : Control
{
    [Export] public SiderealPalette Skin { get; set; } = null!;
    public override void _Ready()
    {
        var palette = Skin != null ? SiderealPalette.Activate(Skin) : SiderealPalette.LoadProfile();
        // The exported palette stays a source-editable asset; local preferences do not overwrite it.
        Theme = palette.CreateTheme();
        var window = new DockWindow("component-gallery", "SIDEREAL COMPONENT GALLERY", new Vector2(40, 60), new Vector2(580, 590));
        AddChild(window);
        window.Content.AddChild(UiKit.Heading("Shared controls", 30));
        window.Content.AddChild(UiKit.Paragraph("Presentation preview. This scene has no database connection or gameplay actions.", 420));
        var row = new HBoxContainer();
        row.AddChild(UiKit.Button("Default button", () => { }));
        var primary = UiKit.Button("Primary action", () => { }); primary.ThemeTypeVariation = "AccentButton"; row.AddChild(primary);
        var disabled = UiKit.Button("Disabled", () => { }); disabled.Disabled = true; row.AddChild(disabled); window.Content.AddChild(row);
        window.Content.AddChild(new LineEdit { PlaceholderText = "Editable field", TooltipText = "The same font, focus outline and input style apply throughout Sidereal." });
        window.Content.AddChild(new CheckBox { Text = "Enabled setting", ButtonPressed = true });
        window.Content.AddChild(new HSlider { MinValue = 0, MaxValue = 100, Value = 62 });
        window.Content.AddChild(new ProgressBar { Value = 62, CustomMinimumSize = new Vector2(0, 20) });
        var healthLabel=new HBoxContainer();healthLabel.AddChild(new VitalIcon());healthLabel.AddChild(UiKit.Label("Health · sample 62%",14));window.Content.AddChild(healthLabel);
        window.Content.AddChild(new ProgressBar {ThemeTypeVariation="HealthBar",Value=62,ShowPercentage=false,CustomMinimumSize=new Vector2(0,6)});
        window.Content.AddChild(UiKit.Panel(UiKit.Paragraph("Nested panel\nPanels, buttons, fields and tooltips inherit one Theme resource.", 420), 0));
        window.Content.AddChild(UiKit.Paragraph("Drag the header or any border. The layout stays inside the viewport and is remembered locally.", 420));
    }
}
