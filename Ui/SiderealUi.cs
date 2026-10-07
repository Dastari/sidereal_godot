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
    private readonly Dictionary<string,StorageWindow> storageWindows=new();
    private readonly List<Action> themePreferenceBindings=new();
    private bool synchronizingThemePreferences;
    private PanelContainer header = null!, footer = null!, login = null!, crew = null!, hud = null!, actionFrame = null!;
    private SystemMenu menu = null!;
    private Control entryBrand = null!;
    private CrewPreviewView selectionPreview = null!;
    private Label status = null!, crewName = null!, crewInfo = null!, identity = null!, hudName = null!, hudState = null!;
    private Label identitySubtitle = null!;
    private Label speed = null!, heading = null!, mass = null!, actionHint = null!, context = null!, loginNotice = null!, crewNotice = null!;
    private Label healthValue = null!, weaponEnergyValue = null!, powerState = null!;
    private Label compactSummary = null!;
    private VBoxContainer fullTelemetry = null!;
    private ProgressBar carry = null!, health = null!, weaponEnergy = null!;
    private HBoxContainer telemetryRow = null!, cargoRow = null!, healthRow = null!, weaponEnergyRow = null!;
    private VBoxContainer loginColumn = null!, crewColumn = null!;
    private Label loginTitle = null!, crewTitle = null!, actionTitle = null!;
    private LineEdit characterName = null!;
    private Button loginButton = null!, enterButton = null!, seatButton = null!, menuButton = null!, settingsButton = null!;
    private Button inventoryNav = null!, equipmentNav = null!, crewNav = null!, signOutNav = null!;
    private Button entryRetry = null!, entryDiscard = null!, entryReturn = null!, entryLeaveSeat = null!;
    private DockWindow inventoryWindow = null!, equipmentWindow = null!, settingsWindow = null!;
    private InventoryWorkspace inventory = null!;
    private EquipmentPanel equipment = null!;
    private HotbarView hotbar = null!;
    private InventoryInteractionView itemInteraction = null!;
    private bool entered, waitingForEntry;
    private bool entryJoinAttempted, entryAwaitingReceipt;
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
    public event Action<Key>? GameplayShortcutRequested;
    public event Action? ResetCameraRequested;
    public event Action<string?>? ObserveBodyRequested;
    public bool InteriorView { get; private set; } = true;
    public void SetViewMode(bool interior) => InteriorView = interior;
    public void SetGraphicsStatus(string effectiveText) { if (menu != null) menu.EffectiveGraphics = effectiveText; }
    public void SetVistas((string Id, string Name)[] values) { if (menu != null) menu.SetVistas(values); }
    public bool OpenContainer(string id)
    {
        if(!WorldVisible&&!(demo||worldPreview))return false;
        var container=InventoryPresentation.Read(core,demo||worldPreview).Container(id);
        if(container?.Kind!="grid")return false;
        if(!storageWindows.TryGetValue(id,out var window))
        {
            if(storageWindows.Count>=6){ShowMessage("Close a storage window before opening another.");return false;}
            window=new StorageWindow(core,demo||worldPreview,container,WindowBounds.Position+new Vector2(260+storageWindows.Count*24,24+storageWindows.Count*24));
            storageWindows[id]=window;windows.Add(window);AddChild(window);window.VisibilityChanged+=()=>{if(!window.IsVisibleInTree())itemInteraction?.CancelLocal();};
            window.ItemInspectRequested+=itemId=>inventory.SelectItem(itemId);
            window.ContainerOpenRequested+=childId=>OpenContainer(childId);
            var captured=window;
            window.Closed+=()=>{core.ReleaseControls();GetViewport().GuiReleaseFocus();storageWindows.Remove(id);windows.Remove(captured);captured.QueueFree();};
            window.AccessLost+=()=>{storageWindows.Remove(id);windows.Remove(captured);captured.QueueFree();};
        }
        menu.Hide();window.Show();window.Clamp();window.BringToFront();core.ReleaseControls();return true;
    }
    public bool BlocksKeyboardInput => !WorldVisible || menu.Visible || inventoryWindow.Visible || equipmentWindow.Visible || storageWindows.Values.Any(w=>w.Visible) ||
        inventory.InteractionActive || equipment.PreviewInteractionActive || windows.Exists(w => w.Visible && w.InteractionActive) || GetViewport().GuiGetFocusOwner() != null;
    public bool BlocksPointerInput => !WorldVisible || menu.Visible || itemInteraction?.Capturing==true || equipment.PreviewInteractionActive || windows.Exists(w=>w.Visible&&w.InteractionActive) || PointerOverUi() || GetViewport().GuiIsDragging();
    public bool BlocksCameraInput => !WorldVisible || menu.Visible || inventory.InteractionActive || equipment.PreviewInteractionActive || windows.Exists(w=>w.Visible&&w.InteractionActive) || GetViewport().GuiIsDragging() || PointerOverUi();
    public bool GameplayShortcutBlocked => menu.Visible || inventory.InteractionActive || equipment.PreviewInteractionActive || windows.Exists(window => window.Visible && window.InteractionActive);
    public bool WorldVisible => !demo && entered && core.Character?.Connected == true;
    public void CancelInteractions()
    {
        foreach(var window in windows)window.CancelInteraction();
        equipment.CancelPreviewInteraction();
        GetViewport().GuiCancelDrag();itemInteraction?.CancelLocal();GetViewport().GuiReleaseFocus();core.ReleaseControls();
    }
    public bool BlocksWorldInput => !WorldVisible || menu.Visible || windows.Exists(w => w.Visible && w.InteractionActive) ||
        inventory.InteractionActive || equipment.PreviewInteractionActive || GetViewport().GuiIsDragging() || GetViewport().GuiGetFocusOwner() != null ||
        GetViewport().GuiGetHoveredControl() != null || PointerOverUi();

    private bool PointerOverUi()
    {
        // Godot does not refresh its hover owner when a frame opens under a stationary pointer.
        var pointer = GetGlobalMousePosition();
        foreach (var panel in new Control[] { header, footer, login, crew, hud, actionFrame, seatButton, menuButton, menu, settingsButton })
            if (panel.IsVisibleInTree() && panel.GetGlobalRect().HasPoint(pointer)) return true;
        if(groundLabels.Values.Any(label=>label.IsVisibleInTree()&&label.GetGlobalRect().HasPoint(pointer)))return true;
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
        palette = SiderealPalette.LoadProfile();
        var preferences = NativePreferences.Current;
        if (preferences.Load(ProjectSettings.GlobalizePath("user://presentation-v1.json")))
        { palette.Opacity = (float)preferences.Snapshot.PanelOpacity; palette.UiScale = (float)preferences.Snapshot.UiScale; }
        else preferences.Set(preferences.Snapshot with { PanelOpacity = palette.Opacity, UiScale = palette.UiScale });
        preferences.Changed += PreferencesChanged;
        Theme = palette.CreateTheme();
        ReadPreviewLayoutArguments();
        palette.Changed += ApplyTheme;
        BuildHeader(); BuildLogin(); BuildCrew(); BuildHud(); BuildWindows(); BuildFooter(); BuildGameplayPanels();
        itemInteraction=new InventoryInteractionView(core,demo||worldPreview)
        {
            TopWindowAt=point=>windows.Where(w=>w.IsVisibleInTree()&&w.GetGlobalRect().HasPoint(point)).OrderByDescending(w=>w.ZIndex).FirstOrDefault(),
            OpenTransferDestination=()=>storageWindows.Values.Where(w=>w.IsVisibleInTree()).OrderByDescending(w=>w.ZIndex).Select(w=>InventoryPresentation.Read(core,demo||worldPreview).Container(w.ContainerId)).FirstOrDefault(c=>c!=null&&!c.Carried&&!c.PlacementId.StartsWith("ground:",StringComparison.Ordinal))?.Id,
            Enabled=()=>!menu.Visible&&!settingsWindow.Visible&&(demo||worldPreview||WorldVisible)
        };
        AddChild(itemInteraction);
        menu.VisibilityChanged+=()=>{if(menu.Visible)itemInteraction.CancelLocal();};
        GetViewport().SizeChanged += QueueLayout;
        hud.MinimumSizeChanged += QueueLayout;
        actionFrame.MinimumSizeChanged += QueueLayout;
        header.MinimumSizeChanged += QueueLayout;
        login.MinimumSizeChanged += QueueLayout;
        status.MinimumSizeChanged += QueueLayout;
        gameplayShell = demo || worldPreview;
        footer.Visible = demo || worldPreview;
        Layout();
        inventoryWindow.Visible = demo && !worldPreview;
        equipmentWindow.Visible = demo && !worldPreview && (CanPairDefaultWindows || UsableBounds.Size.X >= 960);
        settingsWindow.Visible = OS.GetCmdlineUserArgs().Contains("--ui-settings");
        menu.Hide();
        var previewTab=OS.GetCmdlineUserArgs().FirstOrDefault(arg=>arg.StartsWith("--ui-menu=",StringComparison.Ordinal))?.Split('=',2)[1];
        if(previewTab!=null){menu.SelectTab(previewTab);menu.Show();menu.BringToFront();}
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

    private void PreferencesChanged(NativePreferencesSnapshot next)
    {
        if(!Mathf.IsEqualApprox(palette.UiScale,(float)next.UiScale))scaleOverride=0;
        palette.Opacity = (float)next.PanelOpacity; palette.UiScale = (float)next.UiScale;
        palette.ApplyAndSave();
    }

    private void ApplyTheme()
    {
        Theme = palette.CreateTheme();
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
        identity = EllipsisHeading("SIDEREAL", 25); identityColumn.AddChild(identity);
        var subtitle = identitySubtitle = UiKit.Label("", 13); subtitle.ThemeTypeVariation="MutedLabel";
        identityColumn.AddChild(subtitle);
        header = UiKit.Panel(identityColumn, 0, "FramePanel"); header.Name = "ShipIdentity"; AddChild(header);

        entryBrand = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        ((HBoxContainer)entryBrand).AddThemeConstantOverride("separation",8);
        entryBrand.AddChild(new SiderealEmblem());
        var wordmark = UiKit.Heading("SIDEREAL", 64);
        entryBrand.AddChild(wordmark);
        AddChild(entryBrand);

        context = UiKit.Label("", 14); context.HorizontalAlignment = HorizontalAlignment.Center;
        context.MouseFilter = MouseFilterEnum.Ignore; context.ThemeTypeVariation="MutedLabel"; AddChild(context);
        menuButton = UiKit.Button("Menu", () => { itemInteraction?.CancelLocal(); core.ReleaseControls(); menu.Visible = !menu.Visible; if (menu.Visible) menu.BringToFront(); });
        menuButton.CustomMinimumSize = new Vector2(92, 44); menuButton.Name = "Menu"; menuButton.ZIndex = 95; AddChild(menuButton);
        settingsButton = UiKit.Button("Interface", () => Toggle(settingsWindow));
        settingsButton.ThemeTypeVariation = "GhostButton"; settingsButton.ZIndex = 95; AddChild(settingsButton);

        menu = new SystemMenu(core, () => Toggle(inventoryWindow), () => Toggle(equipmentWindow),
            () => { menu.Hide(); entered = false; core.ReleaseControls(); HideGameWindows(); },
            () => { menu.Hide(); entered = false; waitingForEntry = false; HideGameWindows(); signOut(); },
            () => { menu.Hide(); Toggle(settingsWindow); }, () => { foreach (var window in windows) window.ResetLayout(); });
        menu.Name = "SessionMenu"; menu.ZIndex = 100; AddChild(menu); windows.Add(menu);
        menu.GameplayAction = key => { if(key==Key.N)ToggleNavigation();else if(key==Key.Z)ToggleGroundLabels();else GameplayShortcutRequested?.Invoke(key); };
        menu.ResetCamera = () => ResetCameraRequested?.Invoke();
        inventoryNav = menu.InventoryButton; equipmentNav = menu.CharacterButton; crewNav = menu.ReturnButton; signOutNav = menu.SignOutButton;
    }

    private void BuildLogin()
    {
        var column=loginColumn=new VBoxContainer();column.AddThemeConstantOverride("separation",12);
        loginTitle=UiKit.Heading("Sign in",22);column.AddChild(loginTitle);column.AddChild(new HSeparator());
        var description=FlexibleLabel("Sign in with your Dastari account.",14);description.ThemeTypeVariation="MutedLabel";column.AddChild(description);
        loginNotice=FlexibleLabel("",13);loginNotice.Visible=false;column.AddChild(loginNotice);
        loginButton=UiKit.Button("Sign in",signIn);loginButton.AddThemeFontSizeOverride("font_size",18);loginButton.CustomMinimumSize=new Vector2(0,40);column.AddChild(loginButton);
        var registration=FlexibleLabel("Account registration is available on the sign-in page.",12);registration.ThemeTypeVariation="MutedLabel";column.AddChild(registration);
        login=UiKit.Panel(column,0,"FramePanel");login.Name="SignIn";AddChild(login);
    }

    private void BuildCrew()
    {
        var column = crewColumn = new VBoxContainer(); column.AddThemeConstantOverride("separation", 14);
        var title = crewTitle = UiKit.Heading("Character select", 32); column.AddChild(title);
        crewName = EllipsisHeading("Preparing your character…", 30); column.AddChild(crewName);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var details = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        crewInfo = FlexibleLabel("Waiting for your game session.", 18); details.AddChild(crewInfo);
        crewNotice = FlexibleLabel("", 15); crewNotice.ThemeTypeVariation="WarningLabel"; details.AddChild(crewNotice);
        entryRetry=UiKit.Button("Retry shared-system entry",()=>{entryJoinAttempted=false;waitingForEntry=true;notice=null;});details.AddChild(entryRetry);
        entryDiscard=UiKit.Button("Discard saved join request",()=>{if(core.DiscardPendingSharedJoin()){entryJoinAttempted=false;waitingForEntry=false;notice=null;}});details.AddChild(entryDiscard);
        entryReturn=UiKit.Button("Return from construction review",()=>core.ReturnFromReview());details.AddChild(entryReturn);
        entryLeaveSeat=UiKit.Button("Leave control seat",()=>{GetViewport().GuiReleaseFocus();core.ToggleSeat(fromUi:true);});details.AddChild(entryLeaveSeat);

        scroll.AddChild(details); column.AddChild(scroll);
        characterName = new LineEdit { PlaceholderText = "Character name", Text = "Explorer", MaxLength = 40,
            CustomMinimumSize = new Vector2(0, 44), TooltipText = "Create your first character. Existing characters keep their name." };
        characterName.FocusEntered += core.ReleaseControls; column.AddChild(characterName);
        enterButton = UiKit.Button("Enter world", () => RequestEnter()); enterButton.ThemeTypeVariation = "AccentButton";
        enterButton.CustomMinimumSize = new Vector2(0, 56); column.AddChild(enterButton);
        crew = UiKit.Panel(column, 8, "FramePanel"); crew.Name = "CrewSelection"; AddChild(crew);
        selectionPreview=new CrewPreviewView(core) {Name="CharacterSelectionPreview"};AddChild(selectionPreview);selectionPreview.Hide();
    }

    private void BuildHud()
    {
        var column = fullTelemetry = new VBoxContainer(); column.AddThemeConstantOverride("separation", 4);
        hudName = EllipsisHeading("", 20); column.AddChild(hudName);
        hudState = FlexibleLabel("", 12); column.AddChild(hudState);
        var telemetry = telemetryRow = new HBoxContainer(); telemetry.AddThemeConstantOverride("separation", 24);
        speed = UiKit.Label("— m/s", 12); speed.SizeFlagsHorizontal = SizeFlags.ExpandFill; telemetry.AddChild(speed);
        heading = UiKit.Label("—°", 12); telemetry.AddChild(heading); column.AddChild(telemetry);
        healthRow = new HBoxContainer(); healthRow.AddThemeConstantOverride("separation",6);healthRow.AddChild(new VitalIcon());healthRow.AddChild(UiKit.Label("Health", 12)); healthRow.AddChild(UiKit.Spacer());
        healthValue = UiKit.Label("", 12); healthRow.AddChild(healthValue); column.AddChild(healthRow);
        health = new ProgressBar { ThemeTypeVariation="HealthBar", ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6), MouseFilter = MouseFilterEnum.Ignore }; column.AddChild(health);
        weaponEnergyRow = new HBoxContainer(); weaponEnergyRow.AddChild(UiKit.Label("Weapon energy", 12)); weaponEnergyRow.AddChild(UiKit.Spacer());
        weaponEnergyValue = UiKit.Label("", 12); weaponEnergyRow.AddChild(weaponEnergyValue); column.AddChild(weaponEnergyRow);
        weaponEnergy = new ProgressBar { ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6), MouseFilter = MouseFilterEnum.Ignore }; column.AddChild(weaponEnergy);
        var cargo = cargoRow = new HBoxContainer(); cargo.AddChild(UiKit.Label("Carry mass", 12)); cargo.AddChild(UiKit.Spacer());
        mass = UiKit.Label("", 12); cargo.AddChild(mass); column.AddChild(cargo);
        carry = new ProgressBar { ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6), MouseFilter = MouseFilterEnum.Ignore };
        column.AddChild(carry);
        powerState = FlexibleLabel("", 12); column.AddChild(powerState);
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
        actionTitle = UiKit.Heading("ACTION BAR", 16); actionTitle.ThemeTypeVariation="AccentHeading"; actions.AddChild(actionTitle);
        hotbar = new HotbarView(core, demo || worldPreview); actions.AddChild(hotbar);
        hotbar.InspectRequested += id => { core.ReleaseControls(); inventoryWindow.Show(); inventoryWindow.BringToFront(); inventory.SelectItem(id); };
        hotbar.OpenInventoryRequested+=()=>{core.ReleaseControls();inventoryWindow.Show();inventoryWindow.BringToFront();};
        actionFrame = UiKit.Panel(actions, 0, "FramePanel"); actionFrame.Name = "ActionBar"; AddChild(actionFrame);
        actionHint = UiKit.Label("", 14); actionHint.MouseFilter = MouseFilterEnum.Ignore;
        actionHint.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        actionHint.ThemeTypeVariation="MutedLabel"; AddChild(actionHint);
        seatButton = UiKit.Button("E   Use", () => {GetViewport().GuiReleaseFocus();core.Interact(fromUi:true);}); seatButton.Name = "StationInteraction";
        seatButton.CustomMinimumSize = new Vector2(0, 48); AddChild(seatButton);
    }

    private DockWindow Window(string id, string title, Vector2 position, Vector2 size)
    {
        var window = new DockWindow(id, title, position, size); AddChild(window);
        window.Closed += core.ReleaseControls; window.VisibilityChanged+=()=>{if(!window.IsVisibleInTree())itemInteraction?.CancelLocal();}; windows.Add(window); return window;
    }

    private void BuildWindows()
    {
        inventoryWindow = Window("inventory", "INVENTORY", new Vector2(26, 176), new Vector2(680, 485));
        inventory = new InventoryWorkspace(core, demo); inventoryWindow.Content.AddChild(inventory);
        inventory.ContainerOpenRequested+=id=>OpenContainer(id);
        equipmentWindow = Window("equipment", "CHARACTER & EQUIPMENT", new Vector2(727, 176), new Vector2(620, 680));
        inventoryWindow.VisibilityChanged += QueueLayout; equipmentWindow.VisibilityChanged += QueueLayout;
        inventoryWindow.LayoutPreferenceChanged += QueueLayout; equipmentWindow.LayoutPreferenceChanged += QueueLayout;
        equipment = new EquipmentPanel(core, demo); equipmentWindow.Content.AddChild(equipment);
        equipment.InspectRequested+=id=>{core.ReleaseControls();inventoryWindow.Show();inventoryWindow.BringToFront();inventory.SelectItem(id);};
        settingsWindow = Window("interface", "Theme colours", new Vector2(400, 115), new Vector2(445, 510));
        var column = settingsWindow.Content;
        column.AddChild(UiKit.Paragraph("Theme and layout preferences apply throughout Sidereal and are saved on this computer.", 300));
        AddSlider(column, "Panel opacity", .3, 1, .01, ()=>NativePreferences.Current.Snapshot.PanelOpacity, value => NativePreferences.Current.Set(NativePreferences.Current.Snapshot with { PanelOpacity = value }));
        AddSlider(column, "UI scale", .75, 1.5, .05, ()=>NativePreferences.Current.Snapshot.UiScale, value => { scaleOverride = 0; NativePreferences.Current.Set(NativePreferences.Current.Snapshot with { UiScale = value }); });
        AddColor(column, "Panel colour", palette.Surface, value => palette.Surface = value);
        AddColor(column, "Accent colour", palette.Accent, value => palette.Accent = value);
        AddColor(column, "Text colour", palette.Text, value => palette.Text = value);
        AddColor(column, "Secondary text", palette.Muted, value => palette.Muted = value);
        AddColor(column, "Danger / invalid drop", palette.Danger, value => palette.Danger = value);
        AddColor(column, "Health", palette.Health, value => palette.Health = value);
        AddColor(column, "Success", palette.Success, value => palette.Success = value);
        AddColor(column, "Warning", palette.Warning, value => palette.Warning = value);
        AddColor(column, "Common items", palette.Common, value => palette.Common = value);
        AddColor(column, "Uncommon items", palette.Uncommon, value => palette.Uncommon = value);
        AddColor(column, "Legendary items", palette.Legendary, value => palette.Legendary = value);
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

    private void AddSlider(VBoxContainer column, string label, double min, double max, double step, Func<double> getter, Action<double> setter)
    {
        var initial=getter();
        var row = new VBoxContainer(); var value = UiKit.Label($"{label}  {initial:P0}");
        row.AddChild(value);
        var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = initial,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(125, 28) };
        slider.ValueChanged += next => {if(!synchronizingThemePreferences)setter(next);};
        themePreferenceBindings.Add(()=>{var current=getter();slider.Value=current;value.Text=$"{label}  {current:P0}";});
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
        if(!Mathf.IsEqualApprox(Scale.X,userScale))itemInteraction?.CancelLocal();
        Scale = Vector2.One * userScale;
        Size = GetViewportRect().Size / userScale;
        UsableBounds = ResolveUsableBounds(userScale);
        var p = UsableBounds.Position; var e = UsableBounds.End; var a = UsableBounds.Size;
        variant = a.X < 960 ? "Narrow" : a.X < 1280 ? "Compact" : a.X < 1600 ? "Standard" : "Wide";
        shortHeight = a.Y < 720;
        var tightHud = a.X < 720 && shortHeight;
        identitySubtitle.Visible = false;
        header.ThemeTypeVariation = "CompactFramePanel";
        header.Position = p; header.Size = new Vector2(Math.Min(233, a.X * .42f), 52);
        menuButton.Size = new Vector2(100, 48); menuButton.Position = new Vector2(e.X - menuButton.Size.X, p.Y + 2);
        settingsButton.Size = new Vector2(122, 44); settingsButton.Position = new Vector2(e.X - settingsButton.Size.X, p.Y + 2);
        context.Position = new Vector2(p.X + header.Size.X + 12, p.Y + 22);
        context.Size = new Vector2(Math.Max(0, a.X - header.Size.X - menuButton.Size.X - 36), 25);
        context.Visible = gameplayShell && a.X >= 1080;


        var menuWidth = Math.Min(1760, a.X);
        var menuLeft = p.X + (a.X - menuWidth) / 2;
        var cardWidth = Math.Min(a.X < 960 ? 440 : 480, Math.Max(1, a.X));
        var top = p.Y + (shortHeight ? 64 : 112);
        var bottom = e.Y - (shortHeight ? 44 : 54);
        var cardHeight = Math.Min(shortHeight ? 362 : 430, Math.Max(1, bottom - top));
        loginColumn.AddThemeConstantOverride("separation",12);
        crewColumn.AddThemeConstantOverride("separation", shortHeight ? 8 : 14);
        loginTitle.AddThemeFontSizeOverride("font_size",22);
        crewTitle.AddThemeFontSizeOverride("font_size", shortHeight ? 28 : 36);
        loginButton.CustomMinimumSize=new Vector2(0,40);enterButton.CustomMinimumSize=new Vector2(0,shortHeight?44:56);
        var cardX = a.X < 960 ? p.X + (a.X - cardWidth) / 2 : menuLeft + Math.Min(28, menuWidth * .025f);
        var cardY = top + Math.Max(0, (bottom - top - cardHeight) * .42f);
        crew.Position=new Vector2(cardX,cardY);crew.Size=new Vector2(cardWidth,cardHeight);
        selectionPreview.Position=new Vector2(crew.GetRect().End.X+36,Math.Max(top,p.Y+84));
        selectionPreview.Size=new Vector2(Math.Max(1,Math.Min(440,e.X-selectionPreview.Position.X)),Math.Max(1,Math.Min(640,bottom-selectionPreview.Position.Y)));
        var loginWidth=Math.Min(360,a.X);var loginHeight=Math.Max(200,login.GetCombinedMinimumSize().Y);
        login.Size=new Vector2(loginWidth,loginHeight);
        login.Position=new Vector2(p.X+(a.X-loginWidth)/2,Math.Clamp((top+e.Y-loginHeight)/2,p.Y+64,Math.Max(p.Y+64,e.Y-loginHeight)));
        entryBrand.Position=new Vector2(p.X+12,p.Y-3);
        entryBrand.Size=new Vector2(Math.Max(1,menuWidth-180),shortHeight?60:84);
        foreach (var child in entryBrand.GetChildren().OfType<Label>())
            if (child.Text == "SIDEREAL") child.AddThemeFontSizeOverride("font_size",Math.Clamp((int)(a.X*.045),38,80));

        var stackedHud = a.X < 720;
        var hudWidth = stackedHud ? a.X : Math.Min(233, a.X * .39f);
        var actionWidth = stackedHud ? Math.Min(710, a.X) : Math.Min(710, a.X - hudWidth - 20);
        var compactHud = stackedHud && shortHeight;
        fullTelemetry.Visible = !compactHud; compactSummary.Visible = compactHud;
        hud.ThemeTypeVariation = "CompactFramePanel";
        actionFrame.ThemeTypeVariation=shipActions.Visible?"CompactFramePanel":"ActionBarPanel";
        hotbar.SetCompact(compactHud||a.X<500);
        powerState.Visible = !compactHud && core.Power != null;
        carry.Visible = !compactHud && (demo || worldPreview || core.Inventory.Available) && (demo || worldPreview || core.Inventory.CarryLimitKg > 0);
        var telemetryHeight = compactHud ? 36 : Math.Max(130, fullTelemetry.GetCombinedMinimumSize().Y + 28);
        hud.Size = new Vector2(hudWidth, telemetryHeight);
        actionTitle.Visible = !compactHud&&shipActions.Visible;
        var actionHeight = compactHud ? 72f : 104f;
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
        actionHint.Visible = gameplayShell && a.X >= 960 && !shortHeight && NativePreferences.Current.Snapshot.ShowControlHints;
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
        LayoutDefaultPair();
    }

    private bool CanPairDefaultWindows => inventoryWindow != null && equipmentWindow != null &&
        !inventoryWindow.HasUserLayout && !equipmentWindow.HasUserLayout && WindowBounds.Size.X >= EquipmentPairMetrics.MinimumWidth;
    private void LayoutDefaultPair()
    {
        if (inventoryWindow.InteractionActive || equipmentWindow.InteractionActive) return;
        if (inventoryWindow.Visible && equipmentWindow.Visible && CanPairDefaultWindows)
        {
            var metrics = EquipmentPairMetrics.Create(WindowBounds.Position.X, WindowBounds.Size.X,
                inventoryWindow.PreferredPosition.X, equipmentWindow.PreferredPosition.X,
                inventoryWindow.PreferredSize.X, equipmentWindow.PreferredSize.X);
            if (metrics is { } pair)
            {
                inventoryWindow.PresentTemporaryLayout(new Vector2((float)pair.InventoryX, inventoryWindow.PreferredPosition.Y),
                    new Vector2((float)pair.InventoryWidth, inventoryWindow.PreferredSize.Y));
                equipmentWindow.PresentTemporaryLayout(new Vector2((float)pair.EquipmentX, equipmentWindow.PreferredPosition.Y),
                    new Vector2((float)pair.EquipmentWidth, equipmentWindow.PreferredSize.Y));
                return;
            }
        }
        inventoryWindow.RestorePreferredLayout(); equipmentWindow.RestorePreferredLayout();
    }

    private void Toggle(DockWindow window)
    {
        itemInteraction?.CancelLocal(); core.ReleaseControls(); window.Visible = !window.Visible;
        if (window.Visible)
        {
            if(window!=menu)menu.Hide();window.BringToFront();
            if (UsableBounds.Size.X < 960)
                foreach (var other in windows)
                    if (other != window && !(CanPairDefaultWindows && (window == inventoryWindow || window == equipmentWindow) && (other == inventoryWindow || other == equipmentWindow))) other.Hide();
        }
    }
    private void HideGameWindows() { inventoryWindow.Hide();equipmentWindow.Hide();navigation?.Hide();objectDetails?.Hide();foreach(var window in storageWindows.Values.ToArray())window.Close();core.SelectedPlacementId=null; }
    public void ShowMessage(string text) => notice = text;

    public object SmokeFacts() => new {
        themeRoles=new {health=palette.Health.ToHtml(false),danger=palette.Danger.ToHtml(false)},
        inventoryVisible = inventoryWindow.Visible, equipmentVisible = equipmentWindow.Visible, menuOpen = menu.Visible, selectedMenuTab = menu.SelectedTab, navigationVisible = navigation.Visible, objectDetailsVisible = objectDetails.Visible,
        preferences = NativePreferences.Current.Snapshot, keyboardBlocked = BlocksKeyboardInput, pointerBlocked = BlocksPointerInput, cameraBlocked = BlocksCameraInput, dragging = inventoryWindow.InteractionActive,
        itemInteraction=itemInteraction?.SmokeFacts(),itemDragging=inventory.InteractionActive,guiDragging=GetViewport().GuiIsDragging(),windowInteractions=windows.Where(w=>w.InteractionActive).Select(w=>w.LayoutKey).ToArray(),
        entry=new {entered,waitingForEntry,awaitingReceipt=entryAwaitingReceipt,joinAttempted=entryJoinAttempted,phase=core.EnterPhase,message=crewNotice.Text,returnVisible=entryReturn.Visible,retryVisible=entryRetry.Visible,enterDisabled=enterButton.Disabled},
        focus = GetViewport().GuiGetFocusOwner()?.Name.ToString(), hovered = GetViewport().GuiGetHoveredControl()?.Name.ToString(),
        inventoryX = inventoryWindow.GetGlobalRect().Position.X, inventoryY = inventoryWindow.GetGlobalRect().Position.Y,
        inventoryWidth = inventoryWindow.GetGlobalRect().Size.X, inventoryHeight = inventoryWindow.GetGlobalRect().Size.Y,
        inventoryWindow = Rectangle(inventoryWindow), equipmentWindow = Rectangle(equipmentWindow), settingsWindow = Rectangle(settingsWindow), sessionMenu = Rectangle(menu),
        storageWindows=storageWindows.Values.Select(w=>new {id=w.ContainerId,rect=Rectangle(w)}).ToArray(),
        groundLabelsVisible=showGroundLabels, groundLabels=groundLabels.Select(l=>new {id=l.Key,rect=Rectangle(l.Value)}).ToArray(),
        equipmentPreview=equipment.PreviewFacts, selectionPreview=selectionPreview.SmokeFacts(),
        combatFeedback=combatFeedback.Facts(),
        menuControls = Descendants(menu).OfType<Control>().Where(c=>c.IsVisibleInTree() && c is Button or HSlider or LineEdit).Select(c=>new { name=c.Name.ToString(), text=c is Button b?b.Text:c is LineEdit e?e.Text:"", sliderValue=c is HSlider slider?(double?)slider.Value:null, rect=Rectangle(c) }).ToArray(),
        inventory = demo || worldPreview ? DemoInventory.Snapshot : core.Inventory,
        grids = Descendants(inventory).OfType<InventoryGrid>().Select(grid => grid.SmokeGeometry()).ToArray(),
        hotbar = hotbar.VisualSlots.Select(Rectangle).ToArray(),
        equipment = equipment.VisualSlots.Select(Rectangle).ToArray(), equipmentSlots = equipment.SlotFacts, equipmentLayout = equipment.LayoutFacts,
        equipmentPairLayout = new { active = inventoryWindow.Visible && equipmentWindow.Visible && inventoryWindow.TemporaryLayout && equipmentWindow.TemporaryLayout,
            inventoryUserLayout = inventoryWindow.HasUserLayout, equipmentUserLayout = equipmentWindow.HasUserLayout },
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
        var character=core.Character;
        entryAwaitingReceipt=character?.Connected!=true||core.EnterFailed;
        if(entryAwaitingReceipt&&!core.TryEnter(name??character?.Name??characterName.Text))
        { waitingForEntry=false;entryAwaitingReceipt=false;notice=core.EnterMessage;return; }
        characterName.ReleaseFocus();waitingForEntry=true;entryJoinAttempted=false;notice=null;
    }

    public void Refresh(bool authenticating, string message)
    {
        var character = core.Character;
        synchronizingThemePreferences=true;foreach(var binding in themePreferenceBindings)binding();synchronizingThemePreferences=false;
        if(waitingForEntry&&entryAwaitingReceipt&&core.EnterFailed)
        { waitingForEntry=false;entryAwaitingReceipt=false;notice=core.EnterMessage; }
        if(waitingForEntry&&entryAwaitingReceipt&&core.EnterPhase=="confirmed")entryAwaitingReceipt=false;
        if(waitingForEntry&&!entryAwaitingReceipt&&character?.Connected==true)
        {
            var admission=core.Connection?.Db.OwnWorldAdmission.Iter().FirstOrDefault(a=>a.CharacterId==character.Id&&a.ShipId==character.ShipId);
            if(admission!=null&&core.SharedAdmissionReady&&core.SpatialReady){entered=true;waitingForEntry=false;entryJoinAttempted=false;notice=null;}
            else if(!entryJoinAttempted&&!core.SharedJoinPending&&core.CanJoinSharedWorld){entryJoinAttempted=true;core.JoinSharedWorld();}
        }
        if (character == null) entered = false;
        var shell = demo || worldPreview || WorldVisible;
        var shellChanged = shell != gameplayShell; gameplayShell = shell;
        login.Visible = !shell && core.Connection == null;
        crew.Visible = !shell && core.Connection != null;
        selectionPreview.Visible=crew.Visible&&character!=null&&UsableBounds.Size.X>=960&&UsableBounds.Size.Y>=600;
        hud.Visible = actionFrame.Visible = menuButton.Visible = shell;
        header.Visible = shell; entryBrand.Visible = !shell; settingsButton.Visible = !shell;
        inventoryNav.Visible = equipmentNav.Visible = shell;
        inventoryNav.Disabled = equipmentNav.Disabled = !demo && !WorldVisible;
        crewNav.Visible = !demo && !worldPreview && WorldVisible;
        signOutNav.Visible = !demo && !worldPreview && (core.Connection != null || authenticating);
        loginButton.Disabled = authenticating;
        loginButton.Text = authenticating ? "Complete sign-in in your browser" : "Sign in";
        loginNotice.Text = message;
        loginNotice.Visible = login.Visible && message != "Sign in to connect your character.";
        enterButton.Disabled=core.Connection==null||waitingForEntry||core.SharedJoinPending||(character==null&&characterName.Text.Trim().Length<2);
        enterButton.Text=waitingForEntry?(core.SharedJoinPending?"Joining shared system…":"Loading game…"):character==null?"Create character":"Enter game";
        characterName.Visible = character == null;
        crewName.Text = character?.Name ?? "Create character";
        crewInfo.Text = character == null ? "No character is linked to this account. Choose a name between 2 and 40 characters." : $"{core.Ship?.Name ?? "No ship assigned"}\nCharacter linked to this account.";

        var preview = demo || worldPreview;
        identity.Text = worldPreview ? "Wayfarer · Scene preview" : demo ? "Wren · UI preview" : core.CurrentPresentedShip?.Name ?? core.Instance?.Name ?? core.Ship?.Name ?? "Sidereal";
        hudName.Text = preview ? "Sample crew" : character?.Name ?? "Crew";
        var occupied = core.IsPiloting;
        hudState.Text = preview ? "Sample telemetry · No game session" : core.Eva is {} eva ? $"EVA · {eva.Phase}" : core.Vitals?.State is "downed" or "dead" ? core.Vitals.State : core.RecoveringPilot ? "Pilot exit recovery pending" : occupied ? "Helm" : core.Resting ? "Seated" : core.CombatEnabled ? "On foot · Combat" : "On foot";
        context.Text = preview ? "Scene preview" : core.Eva != null ? "EVA" : core.IsPiloting ? "Helm" : InteriorView && core.Instance != null ? "Ship interior" : "Flight view";
        speed.Text = !preview && core.CurrentPresentedShip is { } ship ? $"{Math.Sqrt(ship.Vx * ship.Vx + ship.Vy * ship.Vy):F1} m/s" : "— m/s";
        heading.Text = !preview && core.CurrentPresentedShip is { } current ? $"{((current.Heading * 180 / Math.PI) % 360 + 360) % 360:F0}°" : "—°";
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
        actionHint.Text = occupied ? "W / S thrust   A / D turn   X cruise   E leave station" : "WASD move   Shift sprint   E use";
        var interaction = core.Interaction;
        seatButton.Visible = shell && !preview && character?.Connected == true && interaction != null;
        seatButton.Disabled = interaction?.Enabled != true || core.GameplayPending;
        seatButton.Text = "E   " + (interaction?.Label ?? "Use");

        var text = notice ?? message;
        var oldNotice = status.Text;
        status.Text = preview ? (worldPreview ? "Scene preview · Sample items · No game session" : "UI preview · Sample items · Game actions disabled") : text;
        // Routine state lives in context and telemetry. Rejections and connection errors remain visible.
        footer.Visible = shell && (preview || notice != null || text != "Connected. Server authority active.");
        var matchingAdmission=core.Connection?.Db.OwnWorldAdmission.Iter().Any(a=>a.CharacterId==character?.Id&&a.ShipId==character?.ShipId)==true;
        var ownedDeckAccess=core.Connection?.Db.OwnGameShipAccess.Iter().Any(a=>a.CharacterId==character?.Id&&a.InstanceId==core.Location?.InstanceId)==true;
        var review=waitingForEntry&&core.Location!=null&&!matchingAdmission&&!ownedDeckAccess;
        entryReturn.Visible=review&&core.CanReturnFromReview;entryReturn.Disabled=core.GameplayPending;
        entryLeaveSeat.Visible=review&&core.IsPiloting;entryLeaveSeat.Disabled=core.GameplayPending||core.RecoveringPilot;
        entryRetry.Visible=crew.Visible&&waitingForEntry&&entryJoinAttempted&&!core.SharedJoinPending&&core.SharedJoinPhase is "error" or "blocked" or "idle";
        entryRetry.Disabled=core.SharedJoinPending||core.Connection==null;
        entryDiscard.Visible=crew.Visible&&core.SharedJoinReviewRequired;entryDiscard.Disabled=core.SharedJoinPending;
        crewNotice.Text=waitingForEntry?entryAwaitingReceipt?core.EnterMessage:review?"Return from construction review to enter the shared system.":core.SharedJoinMessage.Length>0?core.SharedJoinMessage:character?.Connected!=true?"Preparing character…":core.Ship==null?"No ship assigned. Waiting for your vessel.":"Loading shared-system views…":text;
        crewNotice.Visible=crew.Visible&&(waitingForEntry||core.SharedJoinReviewRequired||crewNotice.Text!="Connected. Server authority active.");
        inventory.Refresh(); equipment.Refresh();hotbar.AssignmentItemId=inventoryWindow.Visible||storageWindows.Values.Any(w=>w.Visible)?inventory.SelectedItemId:"";hotbar.Refresh(); menu.Refresh();foreach(var window in storageWindows.Values.ToArray())window.Refresh(); RefreshGameplayPanels(preview);
        if (!demo && !WorldVisible && !waitingForEntry) HideGameWindows();
        if (shellChanged || oldHealthVisible != healthRow.Visible || oldWeaponVisible != weaponEnergyRow.Visible || oldPowerVisible != powerState.Visible)
        { if (shellChanged) menu.Hide(); QueueLayout(); }
        if (oldNotice != status.Text) QueueLayout();
    }

    public bool HandleKey(Key key)
    {
        if (key == Key.R && itemInteraction?.Rotate()==true) return true;
        if (key == Key.F3) { itemInteraction?.CancelLocal(); core.ReleaseControls(); menu.SelectTab("Graphics"); menu.Show(); menu.BringToFront(); return true; }
        if (key == Key.F6) { if (GetViewport().GuiGetFocusOwner() != null) GetViewport().GuiReleaseFocus(); else if(menuButton.IsVisibleInTree())menuButton.GrabFocus();else if(loginButton.IsVisibleInTree())loginButton.GrabFocus();else enterButton.GrabFocus(); return true; }
        if (key == Key.Escape)
        {
            core.ReleaseControls();
            if (itemInteraction?.Capturing==true || itemInteraction?.Animating==true || GetViewport().GuiIsDragging()) { GetViewport().GuiCancelDrag(); itemInteraction?.CancelLocal(); return true; }
            if (menu.Visible) { menu.Hide(); GetViewport().GuiReleaseFocus(); return true; }
            var top = windows.Where(w=>w.Visible).OrderByDescending(w=>w.ZIndex).FirstOrDefault();
            if (top != null) { top.Close(); GetViewport().GuiReleaseFocus(); return true; }
            menu.Show(); menu.BringToFront(); return true;
        }
        if (key == Key.N && WorldVisible && GetViewport().GuiGetFocusOwner() is not (LineEdit or TextEdit)) { ToggleNavigation(); return true; }
        if (key == Key.Z && WorldVisible && GetViewport().GuiGetFocusOwner() is not (LineEdit or TextEdit)) { ToggleGroundLabels(); return true; }
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

    }

    public override void _UnhandledInput(InputEvent input)
    {
        if(input is InputEventJoypadButton or InputEventJoypadMotion)
        {
            if(input.IsActionPressed("ui_cancel")){HandleKey(Key.Escape);GetViewport().SetInputAsHandled();}
            else if(GetViewport().GuiGetFocusOwner()==null&&(input.IsActionPressed("ui_accept")||input.IsActionPressed("ui_up")||input.IsActionPressed("ui_down")||input.IsActionPressed("ui_left")||input.IsActionPressed("ui_right")))
            {HandleKey(Key.F6);GetViewport().SetInputAsHandled();}
        }
    }

    public override void _ExitTree()
    {
        NativePreferences.Current.Changed -= PreferencesChanged;
        palette.Changed -= ApplyTheme;
        GetViewport().SizeChanged -= QueueLayout;
        hud.MinimumSizeChanged -= QueueLayout;
        actionFrame.MinimumSizeChanged -= QueueLayout;
        header.MinimumSizeChanged -= QueueLayout;
        login.MinimumSizeChanged -= QueueLayout;
        status.MinimumSizeChanged -= QueueLayout;
    }
}

/// <summary>Temporary geometry for untouched default frames; user layouts remain independent.</summary>
public readonly record struct EquipmentPairMetrics(double InventoryX, double InventoryWidth, double EquipmentX, double EquipmentWidth)
{
    public const double MinimumWidth = 572;
    public static EquipmentPairMetrics? Create(double left, double width, double preferredInventoryX, double preferredEquipmentX,
        double preferredInventoryWidth, double preferredEquipmentWidth)
    {
        if (!double.IsFinite(left) || !double.IsFinite(width) || width < MinimumWidth) return null;
        var right = left + width;
        if (!double.IsFinite(right)) return null;
        var inventoryX = Math.Clamp(double.IsFinite(preferredInventoryX) ? preferredInventoryX : left, left, right - MinimumWidth);
        var inventoryWidth = Math.Clamp(double.IsFinite(preferredInventoryWidth) ? preferredInventoryWidth : 680, 280, right - inventoryX - 292);
        var equipmentWidth = Math.Clamp(double.IsFinite(preferredEquipmentWidth) ? preferredEquipmentWidth : 620, 280, right - inventoryX - inventoryWidth - 12);
        var equipmentX = Math.Clamp(double.IsFinite(preferredEquipmentX) ? preferredEquipmentX : inventoryX + inventoryWidth + 12,
            inventoryX + inventoryWidth + 12, right - equipmentWidth);
        return new(inventoryX, inventoryWidth, equipmentX, equipmentWidth);
    }
}
