using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Sidereal.Native;
using Sidereal.InventoryUi;

namespace Sidereal.Ui;

/// <summary>The frontend UI composition. All game mutations pass through ClientCore.</summary>
public partial class SiderealUi : Control
{
    private readonly ClientCore core;
    private readonly Action signIn, signOut;
    private readonly bool demo;
    private SiderealPalette palette = null!;
    private readonly List<DockWindow> windows = new();
    private PanelContainer header = null!, footer = null!, login = null!, crew = null!, hud = null!;
    private Label status = null!, crewName = null!, crewInfo = null!, hudInfo = null!;
    private LineEdit characterName = null!;
    private Button loginButton = null!, enterButton = null!, seatButton = null!;
    private Button inventoryNav = null!, equipmentNav = null!, crewNav = null!, signOutNav = null!;
    private DockWindow inventoryWindow = null!, equipmentWindow = null!, settingsWindow = null!;
    private InventoryWorkspace inventory = null!;
    private EquipmentPanel equipment = null!;
    private HotbarView hotbar = null!;
    private bool entered, waitingForEntry;
    private string? notice;
    public bool WorldVisible => !demo && entered && core.Character?.Connected == true;
    public bool BlocksWorldInput => !WorldVisible || windows.Exists(w => w.Visible && w.InteractionActive) ||
        inventory.InteractionActive || GetViewport().GuiIsDragging() || GetViewport().GuiGetFocusOwner() != null ||
        GetViewport().GuiGetHoveredControl() != null || PointerOverUi();

    private bool PointerOverUi()
    {
        // Godot's hover owner can remain null when a frame opens beneath a stationary mouse.
        var pointer = GetGlobalMousePosition();
        foreach (var panel in new Control[] { header, footer, login, crew, hud, hotbar })
            if (panel.IsVisibleInTree() && panel.GetGlobalRect().HasPoint(pointer)) return true;
        return windows.Exists(window => window.IsVisibleInTree() && window.GetGlobalRect().HasPoint(pointer));
    }

    public SiderealUi(ClientCore core, Action signIn, Action signOut, bool demo)
    {
        this.core = core; this.signIn = signIn; this.signOut = signOut; this.demo = demo;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Ready()
    {
        palette = SiderealPalette.LoadProfile(); Theme = palette.CreateTheme();
        palette.Changed += ApplyTheme;
        BuildHeader(); BuildLogin(); BuildCrew(); BuildHud(); BuildWindows(); BuildFooter();
        GetViewport().SizeChanged += Layout;
        Layout();
        if (demo) { inventoryWindow.Show(); equipmentWindow.Show(); }
        else { inventoryWindow.Hide(); equipmentWindow.Hide(); }
        settingsWindow.Visible = Array.IndexOf(OS.GetCmdlineUserArgs(), "--ui-settings") >= 0;
    }

    private void ApplyTheme()
    {
        Theme = palette.CreateTheme(); Layout();
    }

    private void BuildHeader()
    {
        var row = new HBoxContainer();
        var wordmark = UiKit.Heading("SIDEREAL", 34); wordmark.CustomMinimumSize = new Vector2(190, 0); row.AddChild(wordmark);
        row.AddChild(UiKit.Spacer());
        inventoryNav = UiKit.Button("Inventory  [I]", () => Toggle(inventoryWindow)); row.AddChild(inventoryNav);
        equipmentNav = UiKit.Button("Character  [C]", () => Toggle(equipmentWindow)); row.AddChild(equipmentNav);
        row.AddChild(UiKit.Button("Interface", () => Toggle(settingsWindow)));
        if (!demo)
        {
            crewNav = UiKit.Button("Crew", () => { entered = false; core.ReleaseControls(); HideGameWindows(); }); row.AddChild(crewNav);
            signOutNav = UiKit.Button("Sign out", () => { entered = false; waitingForEntry = false; HideGameWindows(); signOut(); }); row.AddChild(signOutNav);
        }
        header = new PanelContainer(); header.AddChild(row); AddChild(header);
    }

    private void BuildLogin()
    {
        var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", 18);
        column.AddChild(UiKit.Heading("CREW ACCESS", 34));
        column.AddChild(UiKit.Paragraph("Explore. Build. Survive. Belong.", 360));
        column.AddChild(new HSeparator());
        column.AddChild(UiKit.Heading("Your account. Your crew.", 25));
        column.AddChild(UiKit.Paragraph("Continue with your Dastari account. Your existing characters and ships will be here when you return.", 360));
        loginButton = UiKit.Button("Sign in with Dastari", signIn);
        loginButton.ThemeTypeVariation = "AccentButton"; loginButton.CustomMinimumSize = new Vector2(0, 54); column.AddChild(loginButton);
        column.AddChild(UiKit.Paragraph("Sign-in opens in your browser. Return to Sidereal when it is complete.", 360));
        login = UiKit.Panel(column, 12); AddChild(login);
    }

    private void BuildCrew()
    {
        var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", 15);
        column.AddChild(UiKit.Heading("YOUR CREW", 32));
        crewName = UiKit.Heading("Preparing your character…", 26); column.AddChild(crewName);
        crewInfo = UiKit.Paragraph("Waiting for your server session.", 360); column.AddChild(crewInfo);
        column.AddChild(new HSeparator());
        characterName = new LineEdit { PlaceholderText = "Character name", Text = "Explorer", MaxLength = 24, TooltipText = "Used when creating your first character. Existing characters keep their name." };
        characterName.FocusEntered += core.ReleaseControls; column.AddChild(characterName);
        enterButton = UiKit.Button("Enter world", () => RequestEnter());
        enterButton.ThemeTypeVariation = "AccentButton"; enterButton.CustomMinimumSize = new Vector2(0, 52); column.AddChild(enterButton);
        column.AddChild(UiKit.Paragraph("One account, one persistent crew. Ship access and actions are validated by the game server.", 360));
        crew = UiKit.Panel(column, 12); AddChild(crew);
    }

    private void BuildHud()
    {
        var column = new VBoxContainer();
        column.AddChild(UiKit.Heading("CREW & SHIP", 22));
        hudInfo = UiKit.Label(""); column.AddChild(hudInfo);
        var actions = new HBoxContainer();
        var controlHint = UiKit.Label("WASD · move / take control", 14);
        controlHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        controlHint.CustomMinimumSize = new Vector2(140, 0); controlHint.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        actions.AddChild(controlHint);
        seatButton = UiKit.Button("Pilot station  [E]", core.ToggleSeat); actions.AddChild(seatButton);
        column.AddChild(actions);
        hud = UiKit.Panel(column, 2); AddChild(hud);
        hotbar = new HotbarView(core, demo); AddChild(hotbar);
    }

    private DockWindow Window(string id, string title, Vector2 position, Vector2 size)
    {
        var window = new DockWindow(id, title, position, size); AddChild(window);
        window.Closed += core.ReleaseControls; windows.Add(window); return window;
    }

    private void BuildWindows()
    {
        inventoryWindow = Window("inventory", "INVENTORY", new Vector2(26, 137), new Vector2(680, 485));
        inventory = new InventoryWorkspace(core, demo); inventoryWindow.Content.AddChild(inventory);
        equipmentWindow = Window("equipment", "CHARACTER & EQUIPMENT", new Vector2(727, 137), new Vector2(523, 485));
        equipment = new EquipmentPanel(core, demo); equipmentWindow.Content.AddChild(equipment);
        settingsWindow = Window("interface", "INTERFACE SETTINGS", new Vector2(400, 150), new Vector2(445, 510));
        var column = settingsWindow.Content;
        column.AddChild(UiKit.Paragraph("Shared styling updates every panel immediately. Your theme and window layout are saved on this computer.", 340));
        AddSlider(column, "Panel opacity", 0.35, 1, 0.01, palette.Opacity, value => palette.Opacity = (float)value, true);
        AddSlider(column, "UI scale", 0.8, 1.4, 0.05, palette.UiScale, value => palette.UiScale = (float)value, false);
        AddColor(column, "Panel colour", palette.Surface, value => palette.Surface = value);
        AddColor(column, "Accent colour", palette.Accent, value => palette.Accent = value);
        AddColor(column, "Text colour", palette.Text, value => palette.Text = value);
        AddColor(column, "Secondary text", palette.Muted, value => palette.Muted = value);
        AddColor(column, "Danger / invalid drop", palette.Danger, value => palette.Danger = value);
        AddColor(column, "Success / uncommon", palette.Success, value => palette.Success = value);
        AddColor(column, "Warning / legendary", palette.Warning, value => palette.Warning = value);
        AddColor(column, "Rare items", palette.Rare, value => palette.Rare = value);
        AddColor(column, "Epic items", palette.Epic, value => palette.Epic = value);
        column.AddChild(UiKit.Button("Reset window layout", () => { foreach (var window in windows) window.ResetLayout(); }));
        column.AddChild(UiKit.Paragraph("Drag a panel header to move it. Drag any edge or corner to resize. Smaller panels scroll their content.", 340));
    }

    private void AddColor(VBoxContainer column, string label, Color initial, Action<Color> setter)
    {
        var row = new HBoxContainer(); row.AddChild(UiKit.Label(label)); row.AddChild(UiKit.Spacer());
        var picker = new ColorPickerButton { Color = initial, EditAlpha = false, CustomMinimumSize = new Vector2(70, 30), TooltipText = label };
        picker.ColorChanged += value => { setter(value); palette.ApplyAndSave(); }; row.AddChild(picker); column.AddChild(row);
    }

    private void AddSlider(VBoxContainer column, string label, double min, double max, double step, double initial, Action<double> setter, bool percent)
    {
        var row = new HBoxContainer(); var value = UiKit.Label(percent ? $"{label}  {initial:P0}" : $"{label}  {initial:F2}×");
        value.CustomMinimumSize = new Vector2(190, 0); row.AddChild(value);
        var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = initial, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(125, 28) };
        slider.ValueChanged += next => { setter(next); value.Text = percent ? $"{label}  {next:P0}" : $"{label}  {next:F2}×"; palette.ApplyAndSave(); };
        row.AddChild(slider); column.AddChild(row);
    }

    private void BuildFooter()
    {
        var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", 3);
        status = UiKit.Label(demo ? "UI preview · Sample data · Server actions disabled" : core.Status, 15); column.AddChild(status);
        column.AddChild(UiKit.Label(demo ? "Inventory · Rotate with R · Drag to equip or assign · Move and resize panels · Live theme settings" : "WASD move / pilot    Shift sprint    E pilot station    I inventory    C equipment    Esc release control", 14));
        footer = new PanelContainer(); footer.AddChild(column); AddChild(footer);
    }

    private void Layout()
    {
        Scale = Vector2.One * palette.UiScale;
        Size = GetViewportRect().Size / palette.UiScale;
        header.Position = Vector2.Zero; header.Size = new Vector2(Size.X, 75);
        footer.Size = new Vector2(Size.X, 57); footer.Position = new Vector2(0, Size.Y - footer.Size.Y);
        var panelWidth = Math.Min(438, Size.X - 48);
        login.Position = new Vector2(28, Mathf.Clamp(Size.Y * 0.23f, 95, Math.Max(95, Size.Y - 425))); login.Size = new Vector2(panelWidth, 0);
        crew.Position = new Vector2(28, Mathf.Clamp(Size.Y * 0.23f, 95, Math.Max(95, Size.Y - 450))); crew.Size = new Vector2(panelWidth, 0);
        hud.Position = new Vector2(20, Size.Y - 232); hud.Size = new Vector2(330, 0);
        hotbar.Position = new Vector2(Math.Max(365, (Size.X - 455) / 2), Size.Y - 152); hotbar.Size = new Vector2(Math.Min(455, Size.X - hotbar.Position.X - 20), 86);
        foreach (var window in windows) window.Clamp();
    }

    private void Toggle(DockWindow window)
    {
        core.ReleaseControls(); window.Visible = !window.Visible; if (window.Visible) window.BringToFront();
    }
    private void HideGameWindows() { inventoryWindow.Hide(); equipmentWindow.Hide(); }
    public void ShowMessage(string text) => notice = text;
    public object SmokeFacts() => new { inventoryVisible = inventoryWindow.Visible, dragging = inventoryWindow.InteractionActive,
        focus = GetViewport().GuiGetFocusOwner()?.Name.ToString(), hovered = GetViewport().GuiGetHoveredControl()?.Name.ToString(),
        inventoryX = inventoryWindow.GetGlobalRect().Position.X, inventoryY = inventoryWindow.GetGlobalRect().Position.Y,
        inventory = demo ? DemoInventory.Snapshot : core.Inventory,
        grids = Descendants(inventory).OfType<InventoryGrid>().Select(grid => grid.SmokeGeometry()).ToArray(),
        hotbar = hotbar.GetChildren().OfType<Control>().Select(Rectangle).ToArray(),
        equipment = Descendants(equipment).OfType<EquipmentSlot>().Select(Rectangle).ToArray() };

    private static object Rectangle(Control control)
    {
        var rect = control.GetGlobalRect(); return new { x = rect.Position.X, y = rect.Position.Y, width = rect.Size.X, height = rect.Size.Y };
    }

    private static IEnumerable<Node> Descendants(Node parent)
    {
        foreach (var child in parent.GetChildren()) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    public void RequestEnter(string? name = null)
    {
        if (demo || core.Connection == null) return;
        core.Enter(name ?? characterName.Text); characterName.ReleaseFocus(); waitingForEntry = true; notice = null;
    }

    public void Refresh(bool authenticating, string message)
    {
        var character = core.Character;
        if (waitingForEntry && character?.Connected == true) { entered = true; waitingForEntry = false; }
        if (character == null) entered = false;
        login.Visible = !demo && core.Connection == null;
        crew.Visible = !demo && core.Connection != null && !WorldVisible;
        hud.Visible = demo || WorldVisible; hotbar.Visible = demo || WorldVisible;
        inventoryNav.Disabled = equipmentNav.Disabled = !demo && !WorldVisible;
        inventoryNav.Visible = equipmentNav.Visible = demo || WorldVisible;
        if (!demo) { crewNav.Visible = WorldVisible; signOutNav.Visible = core.Connection != null || authenticating; }
        loginButton.Disabled = authenticating;
        enterButton.Disabled = core.Connection == null || waitingForEntry;
        enterButton.Text = character == null ? "Create character & enter" : "Resume character";
        characterName.Visible = character == null;
        crewName.Text = character?.Name ?? "Your first journey";
        crewInfo.Text = character == null ? "Choose a name to create your persistent character." : $"{core.Ship?.Name ?? "Preparing your ship"}\nYour account's existing character is ready.";
        seatButton.Disabled = demo || character?.Connected != true;
        hudInfo.Text = demo ? "UI preview crew\nDemo vessel · Sample data" : character == null ? "Waiting for crew…" : $"{character.Name} · {core.Ship?.Name ?? "No ship"}\n{(core.IsPiloting ? "Piloting" : "On foot")} · {(core.ControlsClaimed ? "Control requested" : "Input released")}\n{character.LocalX:F1}, {character.LocalY:F1} m";
        status.Text = demo ? "UI preview · Sample data · Server actions disabled" : notice ?? message;
        inventory.Refresh(); equipment.Refresh(); hotbar.Refresh();
        if (!demo && !WorldVisible && !waitingForEntry) HideGameWindows();
    }

    public bool HandleKey(Key key)
    {
        if (key is not (Key.I or Key.C)) return false;
        if (GetViewport().GuiGetFocusOwner() is LineEdit) return false;
        if (!demo && !WorldVisible) return false;
        Toggle(key == Key.I ? inventoryWindow : equipmentWindow); return true;
    }

    public override void _Input(InputEvent input)
    {
        if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
        for (Node? node = GetViewport().GuiGetHoveredControl(); node != null && node != this; node = node.GetParent())
            if (node is DockWindow window) { window.BringToFront(); break; }
    }

    public override void _ExitTree()
    {
        palette.Changed -= ApplyTheme;
        GetViewport().SizeChanged -= Layout;
    }
}
