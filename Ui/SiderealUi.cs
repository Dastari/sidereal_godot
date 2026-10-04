using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sidereal.Native;
using Sidereal.InventoryUi;

namespace Sidereal.Ui;

/// <summary>Native screen composition; game mutations always pass through ClientCore.</summary>
public partial class SiderealUi : Control
{
    private readonly ClientCore core;
    private readonly Action signIn, signOut;
    private readonly bool demo;
    private readonly bool worldPreview = OS.GetCmdlineUserArgs().Contains("--world-preview");
    private SiderealPalette palette = null!;
    private readonly List<DockWindow> windows = new();
    private PanelContainer header = null!, footer = null!, login = null!, crew = null!, hud = null!, actionFrame = null!, menu = null!;
    private Control entryBrand = null!;
    private Label status = null!, crewName = null!, crewInfo = null!, identity = null!, hudName = null!, hudState = null!;
    private Label identitySubtitle = null!;
    private Label speed = null!, heading = null!, mass = null!, actionHint = null!, context = null!, loginNotice = null!;
    private Label heroTagline = null!, heroDetail = null!;
    private Label healthValue = null!, weaponEnergyValue = null!, powerState = null!;
    private Label compactSummary = null!;
    private VBoxContainer fullTelemetry = null!;
    private StyleBoxFlat healthFill = null!;
    private ProgressBar carry = null!, health = null!, weaponEnergy = null!;
    private HBoxContainer telemetryRow = null!, cargoRow = null!, healthRow = null!, weaponEnergyRow = null!;
    private VBoxContainer loginColumn = null!, crewColumn = null!;
    private Label loginTitle = null!, crewTitle = null!, actionTitle = null!;
    private LineEdit characterName = null!;
    private Button loginButton = null!, enterButton = null!, seatButton = null!, menuButton = null!, settingsButton = null!;
    private Button inventoryNav = null!, equipmentNav = null!, crewNav = null!, signOutNav = null!;
    private DockWindow inventoryWindow = null!, equipmentWindow = null!, settingsWindow = null!;
    private InventoryWorkspace inventory = null!;
    private EquipmentPanel equipment = null!;
    private HotbarView hotbar = null!;
    private bool entered, waitingForEntry;
    private bool gameplayShell;
    private string? notice;
    private float scaleOverride;
    private Vector4 insetOverride;
    private string variant = "Standard";
    private bool shortHeight;
    private bool layoutQueued;
    private int layoutPasses;
    public Rect2 UsableBounds { get; private set; }
    public Rect2 WindowBounds { get; private set; }
    public Rect2 WorldPresentationBounds { get; private set; }
    public bool GameplayShortcutBlocked => menu.Visible || inventory.InteractionActive || windows.Exists(window => window.Visible && window.InteractionActive);
    public bool WorldVisible => !demo && entered && core.Character?.Connected == true;
    public bool BlocksWorldInput => !WorldVisible || menu.Visible || windows.Exists(w => w.Visible && w.InteractionActive) ||
        inventory.InteractionActive || GetViewport().GuiIsDragging() || GetViewport().GuiGetFocusOwner() != null ||
        GetViewport().GuiGetHoveredControl() != null || PointerOverUi();

    private bool PointerOverUi()
    {
        // Godot does not refresh its hover owner when a frame opens under a stationary pointer.
        var pointer = GetGlobalMousePosition();
        foreach (var panel in new Control[] { header, footer, login, crew, hud, actionFrame, seatButton, menuButton, menu, settingsButton })
            if (panel.IsVisibleInTree() && panel.GetGlobalRect().HasPoint(pointer)) return true;
        return windows.Exists(window => window.IsVisibleInTree() && window.GetGlobalRect().HasPoint(pointer));
    }

    public SiderealUi(ClientCore core, Action signIn, Action signOut, bool demo)
    {
        this.core = core; this.signIn = signIn; this.signOut = signOut; this.demo = demo;
        MouseFilter = MouseFilterEnum.Ignore;
        // Godot 4.7 rasterizes text at the final scale while retaining font hinting.
        OversamplingWithScale = OversamplingWithScaleEnum.Enabled;
    }

    public override void _Ready()
    {
        palette = SiderealPalette.LoadProfile(); Theme = palette.CreateTheme();
        ReadPreviewLayoutArguments();
        palette.Changed += ApplyTheme;
        BuildHeader(); BuildLogin(); BuildCrew(); BuildHud(); BuildWindows(); BuildFooter();
        GetViewport().SizeChanged += QueueLayout;
        hud.MinimumSizeChanged += QueueLayout;
        actionFrame.MinimumSizeChanged += QueueLayout;
        header.MinimumSizeChanged += QueueLayout;
        status.MinimumSizeChanged += QueueLayout;
        gameplayShell = demo || worldPreview;
        footer.Visible = demo || worldPreview;
        Layout();
        inventoryWindow.Visible = demo && !worldPreview;
        equipmentWindow.Visible = demo && !worldPreview && UsableBounds.Size.X >= 960;
        settingsWindow.Visible = OS.GetCmdlineUserArgs().Contains("--ui-settings");
        menu.Hide();
    }

    private void ReadPreviewLayoutArguments()
    {
        var args = OS.GetCmdlineUserArgs();
        var scale = args.FirstOrDefault(arg => arg.StartsWith("--ui-scale=", StringComparison.Ordinal))?.Split('=', 2)[1];
        if (float.TryParse(scale, NumberStyles.Float, CultureInfo.InvariantCulture, out var requested) && float.IsFinite(requested))
            scaleOverride = Mathf.Clamp(requested, .75f, 1.5f);
        var insets = args.FirstOrDefault(arg => arg.StartsWith("--ui-safe-insets=", StringComparison.Ordinal))?.Split('=', 2)[1]?.Split(',');
        if (insets?.Length == 4)
        {
            var values = new float[4];
            for (var index = 0; index < values.Length; index++)
                if (!float.TryParse(insets[index], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]) || !float.IsFinite(values[index])) return;
            insetOverride = new Vector4(Math.Max(0, values[0]), Math.Max(0, values[1]), Math.Max(0, values[2]), Math.Max(0, values[3]));
        }
    }

    private void ApplyTheme()
    {
        Theme = palette.CreateTheme();
        context.AddThemeColorOverride("font_color", palette.Muted);
        actionHint.AddThemeColorOverride("font_color", palette.Muted);
        if (healthFill != null) healthFill.BgColor = palette.Danger;
        QueueLayout();
    }

    private static Label FlexibleLabel(string text, int size = 17)
    {
        var label = UiKit.Label(text, size);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return label;
    }

    private static Label EllipsisHeading(string text, int size)
    {
        var label = UiKit.Heading(text, size);
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return label;
    }

    private void BuildHeader()
    {
        var identityColumn = new VBoxContainer(); identityColumn.AddThemeConstantOverride("separation", 1);
        identity = EllipsisHeading("SIDEREAL", 29); identityColumn.AddChild(identity);
        var subtitle = identitySubtitle = UiKit.Label("Explore. Build. Survive. Belong.", 13); subtitle.AddThemeColorOverride("font_color", palette.Muted);
        identityColumn.AddChild(subtitle);
        header = UiKit.Panel(identityColumn, 0, "FramePanel"); header.Name = "ShipIdentity"; AddChild(header);

        entryBrand = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        var wordmark = UiKit.Heading("SIDEREAL", 86);
        wordmark.AddThemeColorOverride("font_color", palette.Text); entryBrand.AddChild(wordmark);
        heroTagline = UiKit.Heading("Explore. Build. Survive. Belong.", 25);
        heroTagline.AddThemeColorOverride("font_color", palette.Accent); entryBrand.AddChild(heroTagline);
        heroDetail = FlexibleLabel("A brighter galaxy. Together.", 18);
        heroDetail.AddThemeColorOverride("font_color", palette.Muted); entryBrand.AddChild(heroDetail);
        AddChild(entryBrand);

        context = UiKit.Label("", 14); context.HorizontalAlignment = HorizontalAlignment.Center;
        context.MouseFilter = MouseFilterEnum.Ignore; context.AddThemeColorOverride("font_color", palette.Muted); AddChild(context);
        menuButton = UiKit.Button("Menu", () => { core.ReleaseControls(); menu.Visible = !menu.Visible; });
        menuButton.CustomMinimumSize = new Vector2(92, 44); menuButton.Name = "Menu"; menuButton.ZIndex = 95; AddChild(menuButton);
        settingsButton = UiKit.Button("Interface", () => Toggle(settingsWindow));
        settingsButton.ThemeTypeVariation = "GhostButton"; settingsButton.ZIndex = 95; AddChild(settingsButton);

        var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", 8);
        inventoryNav = UiKit.Button("Inventory  [I]", () => { menu.Hide(); Toggle(inventoryWindow); }); column.AddChild(inventoryNav);
        equipmentNav = UiKit.Button("Character  [C]", () => { menu.Hide(); Toggle(equipmentWindow); }); column.AddChild(equipmentNav);
        column.AddChild(UiKit.Button("Interface settings", () => { menu.Hide(); Toggle(settingsWindow); }));
        crewNav = UiKit.Button("Return to crew", () => { menu.Hide(); entered = false; core.ReleaseControls(); HideGameWindows(); });
        signOutNav = UiKit.Button("Sign out", () => { menu.Hide(); entered = false; waitingForEntry = false; HideGameWindows(); signOut(); });
        column.AddChild(crewNav); column.AddChild(signOutNav);
        menu = UiKit.Panel(column, 0, "FramePanel"); menu.Name = "SessionMenu"; menu.ZIndex = 100; AddChild(menu);
    }

    private void BuildLogin()
    {
        var column = loginColumn = new VBoxContainer(); column.AddThemeConstantOverride("separation", 16);
        var title = loginTitle = UiKit.Heading("CREW ACCESS", 38); title.AddThemeColorOverride("font_color", palette.Accent); column.AddChild(title);
        column.AddChild(FlexibleLabel("Your account. Your crew.", 21));
        column.AddChild(new HSeparator());
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var details = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        loginNotice = FlexibleLabel("", 16); loginNotice.Visible = false; details.AddChild(loginNotice);
        details.AddChild(FlexibleLabel("Continue your journey with your Dastari account. Your characters and ships will be here when you return.", 18));
        details.AddChild(FlexibleLabel("Sign-in opens in your browser. Return to Sidereal when it is complete.", 16));
        scroll.AddChild(details); column.AddChild(scroll);
        loginButton = UiKit.Button("Sign in with Dastari", signIn); loginButton.ThemeTypeVariation = "AccentButton";
        loginButton.CustomMinimumSize = new Vector2(0, 56); column.AddChild(loginButton);
        login = UiKit.Panel(column, 8, "FramePanel"); login.Name = "CrewAccess"; AddChild(login);
    }

    private void BuildCrew()
    {
        var column = crewColumn = new VBoxContainer(); column.AddThemeConstantOverride("separation", 14);
        var title = crewTitle = UiKit.Heading("SELECT CREW", 36); title.AddThemeColorOverride("font_color", palette.Accent); column.AddChild(title);
        crewName = EllipsisHeading("Preparing your character…", 30); column.AddChild(crewName);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var details = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        crewInfo = FlexibleLabel("Waiting for your game session.", 18); details.AddChild(crewInfo);
        details.AddChild(new HSeparator());
        details.AddChild(FlexibleLabel("Your crew is persistent. Resume your character to return to the world.", 16));
        scroll.AddChild(details); column.AddChild(scroll);
        characterName = new LineEdit { PlaceholderText = "Character name", Text = "Explorer", MaxLength = 24,
            CustomMinimumSize = new Vector2(0, 44), TooltipText = "Create your first character. Existing characters keep their name." };
        characterName.FocusEntered += core.ReleaseControls; column.AddChild(characterName);
        enterButton = UiKit.Button("Enter world", () => RequestEnter()); enterButton.ThemeTypeVariation = "AccentButton";
        enterButton.CustomMinimumSize = new Vector2(0, 56); column.AddChild(enterButton);
        crew = UiKit.Panel(column, 8, "FramePanel"); crew.Name = "CrewSelection"; AddChild(crew);
    }

    private void BuildHud()
    {
        var column = fullTelemetry = new VBoxContainer(); column.AddThemeConstantOverride("separation", 7);
        hudName = EllipsisHeading("", 24); column.AddChild(hudName);
        hudState = FlexibleLabel("", 14); column.AddChild(hudState);
        column.AddChild(new HSeparator());
        var telemetry = telemetryRow = new HBoxContainer(); telemetry.AddThemeConstantOverride("separation", 24);
        speed = UiKit.Heading("— m/s", 25); speed.SizeFlagsHorizontal = SizeFlags.ExpandFill; telemetry.AddChild(speed);
        heading = UiKit.Heading("—°", 25); telemetry.AddChild(heading); column.AddChild(telemetry);
        healthRow = new HBoxContainer(); healthRow.AddChild(UiKit.Label("Crew health", 14)); healthRow.AddChild(UiKit.Spacer());
        healthValue = UiKit.Label("", 14); healthRow.AddChild(healthValue); column.AddChild(healthRow);
        health = new ProgressBar { ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6), MouseFilter = MouseFilterEnum.Ignore }; column.AddChild(health);
        healthFill = new StyleBoxFlat { BgColor = palette.Danger }; health.AddThemeStyleboxOverride("fill", healthFill);
        weaponEnergyRow = new HBoxContainer(); weaponEnergyRow.AddChild(UiKit.Label("Weapon energy", 14)); weaponEnergyRow.AddChild(UiKit.Spacer());
        weaponEnergyValue = UiKit.Label("", 14); weaponEnergyRow.AddChild(weaponEnergyValue); column.AddChild(weaponEnergyRow);
        weaponEnergy = new ProgressBar { ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6), MouseFilter = MouseFilterEnum.Ignore }; column.AddChild(weaponEnergy);
        var cargo = cargoRow = new HBoxContainer(); cargo.AddChild(UiKit.Label("Carry mass", 14)); cargo.AddChild(UiKit.Spacer());
        mass = UiKit.Label("", 14); cargo.AddChild(mass); column.AddChild(cargo);
        carry = new ProgressBar { ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6), MouseFilter = MouseFilterEnum.Ignore };
        column.AddChild(carry);
        powerState = FlexibleLabel("", 14); column.AddChild(powerState);
        var telemetryComposition = new VBoxContainer(); telemetryComposition.AddChild(column);
        compactSummary = UiKit.Label("", 15);
        compactSummary.ClipText = true;
        compactSummary.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        compactSummary.HorizontalAlignment = HorizontalAlignment.Center;
        compactSummary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        compactSummary.MouseFilter = MouseFilterEnum.Stop;
        telemetryComposition.AddChild(compactSummary);
        hud = UiKit.Panel(telemetryComposition, 0, "FramePanel"); hud.Name = "CrewTelemetry"; AddChild(hud);

        var actions = new VBoxContainer(); actions.AddThemeConstantOverride("separation", 8);
        actionTitle = UiKit.Heading("ACTION BAR", 16); actionTitle.AddThemeColorOverride("font_color", palette.Accent); actions.AddChild(actionTitle);
        hotbar = new HotbarView(core, demo || worldPreview); actions.AddChild(hotbar);
        actionFrame = UiKit.Panel(actions, 0, "FramePanel"); actionFrame.Name = "ActionBar"; AddChild(actionFrame);
        actionHint = UiKit.Label("", 14); actionHint.MouseFilter = MouseFilterEnum.Ignore;
        actionHint.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        actionHint.AddThemeColorOverride("font_color", palette.Muted); AddChild(actionHint);
        seatButton = UiKit.Button("E   Pilot station", core.ToggleSeat); seatButton.Name = "StationInteraction";
        seatButton.CustomMinimumSize = new Vector2(0, 48); AddChild(seatButton);
    }

    private DockWindow Window(string id, string title, Vector2 position, Vector2 size)
    {
        var window = new DockWindow(id, title, position, size); AddChild(window);
        window.Closed += core.ReleaseControls; windows.Add(window); return window;
    }

    private void BuildWindows()
    {
        inventoryWindow = Window("inventory", "INVENTORY", new Vector2(26, 176), new Vector2(680, 485));
        inventory = new InventoryWorkspace(core, demo); inventoryWindow.Content.AddChild(inventory);
        equipmentWindow = Window("equipment", "CHARACTER & EQUIPMENT", new Vector2(727, 176), new Vector2(523, 485));
        equipment = new EquipmentPanel(core, demo); equipmentWindow.Content.AddChild(equipment);
        settingsWindow = Window("interface", "INTERFACE SETTINGS", new Vector2(400, 115), new Vector2(445, 510));
        var column = settingsWindow.Content;
        column.AddChild(UiKit.Paragraph("Theme and layout preferences apply throughout Sidereal and are saved on this computer.", 300));
        AddSlider(column, "Panel opacity", .35, 1, .01, palette.Opacity, value => palette.Opacity = (float)value);
        AddSlider(column, "UI scale", .75, 1.5, .05, palette.UiScale, value => { scaleOverride = 0; palette.UiScale = (float)value; });
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
        column.AddChild(UiKit.Paragraph("Drag a panel header to move it. Drag any edge or corner to resize. Smaller panels scroll their content.", 300));
    }

    private void AddColor(VBoxContainer column, string label, Color initial, Action<Color> setter)
    {
        var row = new HBoxContainer(); row.AddChild(FlexibleLabel(label)); row.AddChild(UiKit.Spacer());
        var picker = new ColorPickerButton { Color = initial, EditAlpha = false, CustomMinimumSize = new Vector2(58, 32), TooltipText = label };
        picker.ColorChanged += value => { setter(value); palette.ApplyAndSave(); }; row.AddChild(picker); column.AddChild(row);
    }

    private void AddSlider(VBoxContainer column, string label, double min, double max, double step, double initial, Action<double> setter)
    {
        var row = new VBoxContainer(); var value = UiKit.Label($"{label}  {initial:P0}");
        row.AddChild(value);
        var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = initial,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(125, 28) };
        slider.ValueChanged += next => { setter(next); value.Text = $"{label}  {next:P0}"; palette.ApplyAndSave(); };
        row.AddChild(slider); column.AddChild(row);
    }

    private void BuildFooter()
    {
        status = FlexibleLabel("", 15);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(status);
        footer = UiKit.Panel(scroll, 0, "QuietPanel"); footer.Name = "SessionNotice"; footer.ZIndex = 110; AddChild(footer);
    }

    private Rect2 ResolveUsableBounds(float userScale)
    {
        var physical = GetViewportRect().Size;
        var insets = insetOverride;
        if (OS.GetName() is "Android" or "iOS")
        {
            var safe = DisplayServer.GetDisplaySafeArea();
            insets.X = Math.Max(insets.X, safe.Position.X);
            insets.Y = Math.Max(insets.Y, safe.Position.Y);
            insets.Z = Math.Max(insets.Z, physical.X - safe.End.X);
            insets.W = Math.Max(insets.W, physical.Y - safe.End.Y);
        }
        // Desktop deviceScale is 1: the viewport is already in physical pixels. A user scale
        // changes available LU exactly once, and can select a different responsive composition.
        var logical = physical / userScale;
        var edge = Math.Min(24, Math.Min(logical.X, logical.Y) * .04f);
        var origin = new Vector2(insets.X / userScale + edge, insets.Y / userScale + edge);
        var available = logical - origin - new Vector2(insets.Z / userScale + edge, insets.W / userScale + edge);
        return new Rect2(origin, new Vector2(Math.Max(1, available.X), Math.Max(1, available.Y)));
    }

    private void QueueLayout()
    {
        if (layoutQueued || !IsInsideTree()) return;
        layoutQueued = true;
        CallDeferred(nameof(ApplyQueuedLayout));
    }

    private void ApplyQueuedLayout()
    {
        if (!IsInsideTree()) return;
        layoutQueued = false;
        Layout();
    }

    private void Layout()
    {
        if (header == null) return;
        layoutPasses++;
        var userScale = scaleOverride > 0 ? scaleOverride : palette.UiScale;
        Scale = Vector2.One * userScale;
        Size = GetViewportRect().Size / userScale;
        UsableBounds = ResolveUsableBounds(userScale);
        var p = UsableBounds.Position; var e = UsableBounds.End; var a = UsableBounds.Size;
        variant = a.X < 960 ? "Narrow" : a.X < 1280 ? "Compact" : a.X < 1600 ? "Standard" : "Wide";
        shortHeight = a.Y < 720;
        var tightHud = a.X < 720 && shortHeight;
        identitySubtitle.Visible = !tightHud;
        header.ThemeTypeVariation = tightHud ? "CompactFramePanel" : "FramePanel";
        header.Position = p; header.Size = new Vector2(Math.Min(tightHud ? 250 : 300, a.X * .55f), tightHud ? 52 : 76);
        menuButton.Size = new Vector2(100, 48); menuButton.Position = new Vector2(e.X - menuButton.Size.X, p.Y + 2);
        settingsButton.Size = new Vector2(122, 44); settingsButton.Position = new Vector2(e.X - settingsButton.Size.X, p.Y + 2);
        context.Position = new Vector2(p.X + header.Size.X + 12, p.Y + 22);
        context.Size = new Vector2(Math.Max(0, a.X - header.Size.X - menuButton.Size.X - 36), 25);
        context.Visible = gameplayShell && a.X >= 1080;
        menu.Size = new Vector2(Math.Min(240, a.X), 0);
        menu.Position = new Vector2(e.X - menu.Size.X, p.Y + 64);

        var menuWidth = Math.Min(1760, a.X);
        var menuLeft = p.X + (a.X - menuWidth) / 2;
        var cardWidth = Math.Min(a.X < 960 ? 440 : 480, Math.Max(1, a.X));
        var top = p.Y + (shortHeight ? 64 : 112);
        var bottom = e.Y - (shortHeight ? 44 : 54);
        var cardHeight = Math.Min(shortHeight ? 362 : 430, Math.Max(1, bottom - top));
        loginColumn.AddThemeConstantOverride("separation", shortHeight ? 8 : 16);
        crewColumn.AddThemeConstantOverride("separation", shortHeight ? 8 : 14);
        loginTitle.AddThemeFontSizeOverride("font_size", shortHeight ? 30 : 38);
        crewTitle.AddThemeFontSizeOverride("font_size", shortHeight ? 28 : 36);
        loginButton.CustomMinimumSize = enterButton.CustomMinimumSize = new Vector2(0, shortHeight ? 44 : 56);
        var cardX = a.X < 960 ? p.X + (a.X - cardWidth) / 2 : menuLeft + Math.Min(28, menuWidth * .025f);
        var cardY = top + Math.Max(0, (bottom - top - cardHeight) * .42f);
        login.Position = crew.Position = new Vector2(cardX, cardY);
        login.Size = crew.Size = new Vector2(cardWidth, cardHeight);
        entryBrand.Position = new Vector2(menuLeft + 24, p.Y - 7);
        entryBrand.Size = new Vector2(Math.Max(1, menuWidth - 180), shortHeight ? 90 : 160);
        foreach (var child in entryBrand.GetChildren().OfType<Label>())
            if (child.Text == "SIDEREAL") child.AddThemeFontSizeOverride("font_size", shortHeight ? 56 : 80);
        heroTagline.Visible = !shortHeight;
        heroDetail.Visible = !shortHeight && a.X >= 960;

        var stackedHud = a.X < 720;
        var hudWidth = stackedHud ? a.X : Math.Min(310, a.X * .39f);
        var actionWidth = stackedHud ? Math.Min(412, a.X) : Math.Min(452, a.X - hudWidth - 20);
        var compactHud = stackedHud && shortHeight;
        fullTelemetry.Visible = !compactHud; compactSummary.Visible = compactHud;
        hud.ThemeTypeVariation = actionFrame.ThemeTypeVariation = compactHud ? "CompactFramePanel" : "FramePanel";
        powerState.Visible = !compactHud && core.Power != null;
        carry.Visible = !compactHud && (demo || worldPreview || core.Inventory.Available) && (demo || worldPreview || core.Inventory.CarryLimitKg > 0);
        var telemetryHeight = compactHud ? 36 : (shortHeight ? 151 : 163);
        if (!compactHud && !demo && !worldPreview && core.Vitals != null) telemetryHeight += 35;
        if (!compactHud && !demo && !worldPreview && core.Combat?.WeaponItemId.Length > 0) telemetryHeight += 35;
        if (!compactHud && powerState.Visible) telemetryHeight += 24;
        hud.Size = new Vector2(hudWidth, telemetryHeight);
        actionTitle.Visible = !compactHud;
        var actionHeight = compactHud ? 80f : 125f;
        actionFrame.Size = new Vector2(actionWidth, actionHeight);
        var actionX = stackedHud ? p.X + (a.X - actionWidth) / 2 : Math.Max(p.X + hudWidth + 16, p.X + (a.X - actionWidth) / 2);
        actionX = Math.Min(actionX, e.X - actionWidth);
        // Native containers own intrinsic heights. Anchor their actual arranged rectangles rather
        // than assuming a font, long value or newly available stat keeps a hardcoded panel height.
        hud.Position = new Vector2(p.X, e.Y - hud.Size.Y);
        actionFrame.Position = new Vector2(actionX, compactHud ? hud.Position.Y - actionFrame.Size.Y - 8 : e.Y - actionFrame.Size.Y);
        if (stackedHud && !compactHud) hud.Position = new Vector2(p.X, actionFrame.Position.Y - hud.Size.Y - 14);
        actionHint.Position = new Vector2(actionFrame.Position.X, actionFrame.Position.Y - 25);
        actionHint.Size = new Vector2(actionWidth, 22);
        actionHint.Visible = gameplayShell && a.X >= 960 && !shortHeight;
        var interactionWidth = tightHud ? Math.Min(220, menuButton.Position.X - header.GetRect().End.X - 24) : Math.Min(278, a.X);
        seatButton.Size = new Vector2(interactionWidth, 50);
        var trailingSpace = e.X - actionFrame.GetRect().End.X;
        var topInteraction = tightHud || (!stackedHud && shortHeight && a.X < 960 &&
            menuButton.Position.X - header.GetRect().End.X - 24 >= interactionWidth);
        seatButton.Position = topInteraction
            ? new Vector2(header.GetRect().End.X + 12, p.Y + 2)
            : !stackedHud && trailingSpace >= interactionWidth + 16
            ? new Vector2(e.X - interactionWidth, e.Y - 53)
            : new Vector2(e.X - interactionWidth, actionFrame.Position.Y - 64);

        footer.Size = new Vector2(Math.Min(gameplayShell && !tightHud ? 540 : 620, a.X), Mathf.Clamp(status.GetMinimumSize().Y + 20, 38, 72));
        footer.Position = gameplayShell
            ? new Vector2(p.X, p.Y + header.Size.Y + 12)
            : new Vector2(p.X, e.Y - Math.Max(40, footer.Size.Y));
        // Identity and persistent session notices own a separate lane. A saved window or a
        // pointer drag cannot place its title under either control, even when a notice appears.
        var windowTop = gameplayShell ? header.GetRect().End.Y + 12 : p.Y + (shortHeight ? 64 : 112);
        if (gameplayShell && footer.Visible) windowTop = Math.Max(windowTop, footer.GetRect().End.Y + 12);
        windowTop = Mathf.Clamp(windowTop, p.Y, Math.Max(p.Y, e.Y - Math.Min(180, a.Y)));
        WindowBounds = new Rect2(new Vector2(p.X, windowTop), new Vector2(a.X, e.Y - windowTop));
        var worldBottom = Math.Min(hud.Position.Y, actionFrame.Position.Y) - 12;
        var worldArea = new Rect2(new Vector2(p.X, windowTop), new Vector2(a.X, Math.Max(0, worldBottom - windowTop)));
        if (!stackedHud)
        {
            // A left telemetry column does not consume the whole lower world. Compare its
            // wide upper area with the taller clear area beside it; narrow screens benefit
            // from the latter, while an ample desktop can keep its centered composition.
            var right = hud.GetRect().End.X + 16;
            var rightBottom = actionFrame.Position.Y - 12;
            var rightArea = new Rect2(new Vector2(right, windowTop), new Vector2(Math.Max(0, e.X - right), Math.Max(0, rightBottom - windowTop)));
            if (seatButton.Visible && rightArea.Intersects(seatButton.GetRect()))
                rightArea.Size = new Vector2(rightArea.Size.X, Math.Max(0, seatButton.Position.Y - 12 - windowTop));
            if (rightArea.Size.X * rightArea.Size.Y > worldArea.Size.X * worldArea.Size.Y) worldArea = rightArea;
        }
        WorldPresentationBounds = gameplayShell && worldArea.Size.X > 0 && worldArea.Size.Y > 0
            ? GetGlobalTransformWithCanvas() * worldArea : default;
        foreach (var window in windows) window.Clamp();
    }

    private void Toggle(DockWindow window)
    {
        core.ReleaseControls(); window.Visible = !window.Visible;
        if (window.Visible) { window.BringToFront(); if (UsableBounds.Size.X < 960) foreach (var other in windows) if (other != window) other.Hide(); }
    }
    private void HideGameWindows() { inventoryWindow.Hide(); equipmentWindow.Hide(); }
    public void ShowMessage(string text) => notice = text;

    public object SmokeFacts() => new {
        inventoryVisible = inventoryWindow.Visible, equipmentVisible = equipmentWindow.Visible, menuOpen = menu.Visible, dragging = inventoryWindow.InteractionActive,
        focus = GetViewport().GuiGetFocusOwner()?.Name.ToString(), hovered = GetViewport().GuiGetHoveredControl()?.Name.ToString(),
        inventoryX = inventoryWindow.GetGlobalRect().Position.X, inventoryY = inventoryWindow.GetGlobalRect().Position.Y,
        inventoryWidth = inventoryWindow.GetGlobalRect().Size.X, inventoryHeight = inventoryWindow.GetGlobalRect().Size.Y,
        inventoryWindow = Rectangle(inventoryWindow), equipmentWindow = Rectangle(equipmentWindow), settingsWindow = Rectangle(settingsWindow), sessionMenu = Rectangle(menu),
        inventory = demo || worldPreview ? DemoInventory.Snapshot : core.Inventory,
        grids = Descendants(inventory).OfType<InventoryGrid>().Select(grid => grid.SmokeGeometry()).ToArray(),
        hotbar = hotbar.GetChildren().OfType<Control>().Select(Rectangle).ToArray(),
        equipment = Descendants(equipment).OfType<EquipmentSlot>().Select(Rectangle).ToArray(),
        telemetryChildren = fullTelemetry.GetChildren().OfType<Control>().Select(control => new {
            name = control.Name.ToString(), minimum = new { width = control.GetCombinedMinimumSize().X, height = control.GetCombinedMinimumSize().Y }, rect = Rectangle(control) }).ToArray(),
        telemetryMinimum = new { width = hud.GetCombinedMinimumSize().X, height = hud.GetCombinedMinimumSize().Y },
        responsive = new { variant, shortHeight, layoutPasses, layoutQueued, scale = Scale.X, usable = new { x = UsableBounds.Position.X, y = UsableBounds.Position.Y, width = UsableBounds.Size.X, height = UsableBounds.Size.Y },
            windowBounds = new { x = WindowBounds.Position.X, y = WindowBounds.Position.Y, width = WindowBounds.Size.X, height = WindowBounds.Size.Y },
            worldPresentationBounds = new { x = WorldPresentationBounds.Position.X, y = WorldPresentationBounds.Position.Y, width = WorldPresentationBounds.Size.X, height = WorldPresentationBounds.Size.Y },
            identity = Rectangle(header), telemetry = Rectangle(hud), action = Rectangle(actionFrame), interaction = Rectangle(seatButton), menu = Rectangle(menuButton),
            login = Rectangle(login), crew = Rectangle(crew), notice = Rectangle(footer) }
    };

    private static object Rectangle(Control control)
    {
        var rect = control.GetGlobalRect();
        return new { x = rect.Position.X, y = rect.Position.Y, width = rect.Size.X, height = rect.Size.Y, visible = control.IsVisibleInTree() };
    }
    private static IEnumerable<Node> Descendants(Node parent)
    {
        foreach (var child in parent.GetChildren()) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    public void RequestEnter(string? name = null)
    {
        if (demo || worldPreview || core.Connection == null) return;
        core.Enter(name ?? characterName.Text); characterName.ReleaseFocus(); waitingForEntry = true; notice = null;
    }

    public void Refresh(bool authenticating, string message)
    {
        var character = core.Character;
        if (waitingForEntry && character?.Connected == true) { entered = true; waitingForEntry = false; }
        if (character == null) entered = false;
        var shell = demo || worldPreview || WorldVisible;
        var shellChanged = shell != gameplayShell; gameplayShell = shell;
        login.Visible = !shell && core.Connection == null;
        crew.Visible = !shell && core.Connection != null;
        hud.Visible = actionFrame.Visible = menuButton.Visible = shell;
        header.Visible = shell; entryBrand.Visible = !shell; settingsButton.Visible = !shell;
        inventoryNav.Visible = equipmentNav.Visible = shell;
        inventoryNav.Disabled = equipmentNav.Disabled = !demo && !WorldVisible;
        crewNav.Visible = !demo && !worldPreview && WorldVisible;
        signOutNav.Visible = !demo && !worldPreview && (core.Connection != null || authenticating);
        loginButton.Disabled = authenticating;
        loginButton.Text = authenticating ? "Complete sign-in in your browser" : "Sign in with Dastari";
        loginNotice.Text = message;
        loginNotice.Visible = login.Visible && message != "Sign in to connect your character.";
        enterButton.Disabled = core.Connection == null || waitingForEntry;
        enterButton.Text = waitingForEntry ? "Entering world…" : character == null ? "Create character & enter" : "Enter world";
        characterName.Visible = character == null;
        crewName.Text = character?.Name ?? "Your first journey";
        crewInfo.Text = character == null ? "Choose a name to create your persistent character." : $"{core.Ship?.Name ?? "Preparing your ship"}\nYour existing character is ready.";

        var preview = demo || worldPreview;
        identity.Text = worldPreview ? "Wayfarer · Scene preview" : demo ? "Wren · UI preview" : core.Ship?.Name ?? "Sidereal";
        hudName.Text = preview ? "Sample crew" : character?.Name ?? "Crew";
        var occupied = core.IsPiloting || core.Seat != null;
        hudState.Text = preview ? "Sample telemetry · No game session" : occupied ? "Control station occupied" : "On foot";
        context.Text = preview ? "Scene preview" : core.Flight?.Active == true ? "In flight" : core.Instance != null ? "Ship interior" : "World";
        speed.Text = !preview && core.Ship is { } ship ? $"{Math.Sqrt(ship.Vx * ship.Vx + ship.Vy * ship.Vy):F1} m/s" : "— m/s";
        heading.Text = !preview && core.Ship is { } current ? $"{((current.Heading * 180 / Math.PI) % 360 + 360) % 360:F0}°" : "—°";
        var snapshot = InventoryPresentation.Read(core, preview);
        var oldHealthVisible = healthRow.Visible; var oldWeaponVisible = weaponEnergyRow.Visible; var oldPowerVisible = powerState.Visible;
        healthRow.Visible = health.Visible = !preview && core.Vitals != null;
        if (core.Vitals is { } vitals)
        {
            healthValue.Text = $"{vitals.Health:F0} / {vitals.MaxHealth:F0}";
            health.MaxValue = Math.Max(.001, vitals.MaxHealth); health.Value = Math.Max(0, vitals.Health);
        }
        weaponEnergyRow.Visible = weaponEnergy.Visible = !preview && core.Combat?.WeaponItemId.Length > 0;
        if (core.Combat is { } combat)
        {
            weaponEnergyValue.Text = $"{combat.Energy:F0} / {combat.Capacity:F0}";
            weaponEnergy.MaxValue = Math.Max(.001, combat.Capacity); weaponEnergy.Value = Math.Max(0, combat.Energy);
        }
        powerState.Text = core.Power is { } power ? $"Ship power: {(power.Brownout ? "Brownout" : power.CorePowered ? "Online" : "Offline")}" : "";
        powerState.Visible = core.Power != null && !(UsableBounds.Size.X < 720 && shortHeight);
        mass.Text = snapshot.Available ? $"{snapshot.CarriedMassKg:F1} / {snapshot.CarryLimitKg:F0} kg" : "Unavailable";
        carry.Visible = snapshot.Available && snapshot.CarryLimitKg > 0 && !(UsableBounds.Size.X < 720 && shortHeight);
        carry.MaxValue = Math.Max(.001, snapshot.CarryLimitKg); carry.Value = Math.Max(0, snapshot.CarriedMassKg);
        compactSummary.Text = (healthRow.Visible ? $"Crew health {healthValue.Text}   " : preview ? "Sample telemetry   " : "") +
            $"Speed {speed.Text}   Mass {mass.Text}";
        compactSummary.TooltipText = $"{hudName.Text}\n{hudState.Text}\n" +
            (healthRow.Visible ? $"Crew health: {healthValue.Text}\n" : "") +
            $"Speed: {speed.Text}\nHeading: {heading.Text}\nCarry mass: {mass.Text}" +
            (weaponEnergyRow.Visible ? $"\nWeapon energy: {weaponEnergyValue.Text}" : "") +
            (powerState.Text.Length > 0 ? $"\n{powerState.Text}" : "");
        actionHint.Text = occupied ? "W / S thrust   A / D turn   E leave station" : "WASD move   Shift sprint   E pilot station";
        seatButton.Visible = shell && !preview && character?.Connected == true;
        seatButton.Disabled = !seatButton.Visible;
        seatButton.Text = occupied ? "E   Leave control station" : "E   Pilot station";

        var text = notice ?? message;
        var oldNotice = status.Text;
        status.Text = preview ? (worldPreview ? "Scene preview · Sample items · No game session" : "UI preview · Sample items · Game actions disabled") : text;
        // Routine state lives in context and telemetry. Rejections and connection errors remain visible.
        footer.Visible = preview || (login.Visible ? notice != null : !shell || notice != null || text != "Connected. Server authority active.");
        inventory.Refresh(); equipment.Refresh(); hotbar.Refresh();
        if (!demo && !WorldVisible && !waitingForEntry) HideGameWindows();
        if (shellChanged || oldHealthVisible != healthRow.Visible || oldWeaponVisible != weaponEnergyRow.Visible || oldPowerVisible != powerState.Visible)
        { if (shellChanged) menu.Hide(); QueueLayout(); }
        if (oldNotice != status.Text) QueueLayout();
    }

    public bool HandleKey(Key key)
    {
        if (key == Key.Escape && menu.Visible) { menu.Hide(); core.ReleaseControls(); return true; }
        if (key is not (Key.I or Key.C)) return false;
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit) return false;
        if (!demo && !WorldVisible) return false;
        Toggle(key == Key.I ? inventoryWindow : equipmentWindow); return true;
    }

    public override void _Input(InputEvent input)
    {
        if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
        for (Node? node = GetViewport().GuiGetHoveredControl(); node != null && node != this; node = node.GetParent())
            if (node is DockWindow window) { window.BringToFront(); break; }
        if (menu.Visible && !menu.GetGlobalRect().HasPoint(GetGlobalMousePosition()) && !menuButton.GetGlobalRect().HasPoint(GetGlobalMousePosition())) menu.Hide();
    }

    public override void _ExitTree()
    {
        palette.Changed -= ApplyTheme;
        GetViewport().SizeChanged -= QueueLayout;
        hud.MinimumSizeChanged -= QueueLayout;
        actionFrame.MinimumSizeChanged -= QueueLayout;
        header.MinimumSizeChanged -= QueueLayout;
        status.MinimumSizeChanged -= QueueLayout;
    }
}
