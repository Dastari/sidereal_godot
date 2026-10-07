using Godot;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sidereal.Native;
using Sidereal.Native.Input;
using Sidereal.Ui;

public partial class Main : Node3D
{
    private ClientCore core = null!;
    private NativeAuth auth = null!;
    private Credential? credential;
    private Task<Credential>? authTask;
    private CancellationTokenSource authCancel = new();
    private readonly ConcurrentQueue<string> browserUrls = new();
    private SiderealUi ui = null!;
    private FrontendBackdrop backdrop = null!;
    private ReplicatedWorld world = null!;
    private PresentationDisplay display = null!;
    private SpaceCombatEffects combatEffects = null!;
    private readonly GameplayKeyState gameplayKeys = new();
    private ulong gameplayEpoch;
    private bool keyboardWasAllowed, orbiting;
    private NativePreferencesSnapshot? appliedPreferences;
    private bool focused = true, signingOut;
    private bool refreshing;
    private DateTimeOffset refreshRetryAt;
    private string? authMessage;
    private string? capturePath;
    private int frames;
    private int captureAfter = 100;
    private bool uiSmoke, smokeEntered, worldPreview;
    private string? smokeStatePath;
    private string? smokeDocumentPath;
    private double smokeStateClock;
    private string? smokeLastKey;
    private string? smokeObserveKind, smokeObserveAppearance;
    private bool smokeFlightView, smokePresentationApplied;
    private string? diagnosticQuitPath;

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        var settingsPath = args.FirstOrDefault(a => a.StartsWith("--settings="))?.Split('=', 2)[1];
        var json = settingsPath == null ? Godot.FileAccess.GetFileAsString("res://client-settings.json") : File.ReadAllText(settingsPath);
        var settings = ClientSettings.Parse(json);
        uiSmoke = args.Contains("--ui-smoke");
        worldPreview = args.Contains("--world-preview");
        if (uiSmoke || worldPreview || args.Contains("--ui-demo"))
            diagnosticQuitPath = args.FirstOrDefault(a => a.StartsWith("--ui-quit-request="))?.Split('=', 2)[1];
        if (uiSmoke && (settingsPath == null || !settings.IsIsolatedFixture))
            throw new InvalidOperationException("UI smoke requires explicit settings for the isolated local or Tailscale test database.");
        smokeStatePath = uiSmoke ? args.FirstOrDefault(a => a.StartsWith("--ui-smoke-state="))?.Split('=', 2)[1] : null;
        smokeDocumentPath = uiSmoke ? args.FirstOrDefault(a => a.StartsWith("--ui-smoke-document="))?.Split('=', 2)[1] : null;
        smokeObserveKind = uiSmoke ? args.FirstOrDefault(a => a.StartsWith("--observe-kind="))?.Split('=', 2)[1] : null;
        smokeObserveAppearance = uiSmoke ? args.FirstOrDefault(a => a.StartsWith("--observe-appearance="))?.Split('=', 2)[1] : null;
        smokeFlightView = uiSmoke && args.Contains("--flight-view");
        if (args.Contains("--ui-demo")) smokeStatePath ??= args.FirstOrDefault(a => a.StartsWith("--ui-demo-state="))?.Split('=', 2)[1];
        if (worldPreview) smokeStatePath ??= args.FirstOrDefault(a => a.StartsWith("--world-preview-state="))?.Split('=', 2)[1];
        core = new ClientCore(settings);
        core.ConfigureSharedJoinJournal(ProjectSettings.GlobalizePath("user://shared-join-v1"));
        core.LoadGameplayGeometry(Godot.FileAccess.GetFileAsString("res://Assets/Environment/gameplay-geometry.json"),
            Godot.FileAccess.GetFileAsString("res://Assets/World/manifest.json"));
        auth = new NativeAuth(settings);
        capturePath = args.FirstOrDefault(a => a.StartsWith("--capture="))?.Split('=', 2)[1];
        var captureFrame = args.FirstOrDefault(a => a.StartsWith("--capture-after="))?.Split('=', 2)[1];
        if (int.TryParse(captureFrame, out var requestedFrame)) captureAfter = Math.Clamp(requestedFrame, 1, 20000);
        BuildWorld();
        world.SetPreview(worldPreview);
        display = new PresentationDisplay(); AddChild(display);
        var layer = new CanvasLayer { Layer = 10 }; AddChild(layer);
        ui = new SiderealUi(core, StartSignIn, SignOut, args.Contains("--ui-demo")); layer.AddChild(ui);
        ui.SetVistas(world.VistaIds.Select(id => (id, id.Replace('-', ' '))).ToArray());
        ui.GameplayShortcutRequested += GameplayShortcut;
        ui.ResetCameraRequested += () => { ClearGameplayInput(); world.ResetCamera(); };
        ui.ObserveBodyRequested += id => { ClearGameplayInput(); world.ObserveBody(id); };
        core.OpenStorageRequested += id => { ClearGameplayInput(); ui.OpenContainer(id); };
        core.PresentationViewChanged += () => { ClearGameplayInput(); world.SetViewMode(core.InteriorView); ui.SetViewMode(core.InteriorView); };
        if (uiSmoke) core.Connect("", provider: false);
        GetWindow().FocusExited += () => { focused = false; ClearGameplayInput(); ui.CancelInteractions(); };
        GetWindow().FocusEntered += () => focused = true;
    }

    private void BuildWorld()
    {
        world = new ReplicatedWorld(); AddChild(world);
        backdrop = new FrontendBackdrop(); AddChild(backdrop);
        combatEffects = new SpaceCombatEffects(); AddChild(combatEffects);
    }
    private void StartSignIn()
    {
        if (authTask != null) return;
        signingOut = false;
        refreshing = false; authMessage = null;
        authCancel.Cancel(); authCancel.Dispose(); authCancel = new CancellationTokenSource();
        authTask = auth.SignIn(url => browserUrls.Enqueue(url), authCancel.Token);
    }

    private void SignOut()
    {
        signingOut = true;
        authCancel.Cancel(); credential = null; core.Dispose();
        authMessage = null;
        // Clear the local session. The system browser's provider SSO cookie is independent.
    }

    public override void _Process(double delta)
    {
        if (diagnosticQuitPath != null && File.Exists(diagnosticQuitPath))
        { ClearGameplayInput(); ui.CancelInteractions(); GetTree().Quit(); return; }
        while (browserUrls.TryDequeue(out var url)) if (!signingOut) OS.ShellOpen(url);
        if (authTask?.IsCompleted == true)
        {
            try
            {
                var result = authTask.GetAwaiter().GetResult();
                if (!signingOut) { credential = result; core.Connect(result.IdToken); authMessage = null; }
            }
            catch
            {
                if (!signingOut)
                {
                    if (refreshing && credential?.ExpiresAt > DateTimeOffset.UtcNow)
                    { refreshRetryAt = DateTimeOffset.UtcNow.AddSeconds(10); authMessage = "Session renewal delayed. Retrying…"; }
                    else { credential = null; core.Dispose(); authMessage = "Sign-in failed or timed out. Try signing in again."; }
                }
            }
            authTask = null; signingOut = false;
        }
        if (credential != null && credential.ExpiresAt <= DateTimeOffset.UtcNow) { credential = null; core.Dispose(); }
        if (credential != null && authTask == null && DateTimeOffset.UtcNow >= refreshRetryAt && credential.ExpiresAt - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(60))
        { refreshing = true; authTask = auth.Refresh(credential, authCancel.Token); }
        core.Tick();
        ui.SetViewMode(core.InteriorView);
        if (uiSmoke && !smokeEntered && core.Connection != null) { smokeEntered = true; ui.RequestEnter("UI Smoke Crew"); }
        ui.Refresh(authTask != null, authTask != null ? (refreshing ? "Renewing game session…" : "Complete sign-in in your browser, then return here.") : authMessage ?? core.Status);
        var worldVisible = ui.WorldVisible || worldPreview;
        var preferences = NativePreferences.Current.Snapshot;
        if (preferences != appliedPreferences)
        {
            appliedPreferences = preferences;
            world.ApplyPreferences(preferences); backdrop.ApplyPreferences(preferences); display.ApplyPreferences(preferences);
            GetViewport().Msaa3D = preferences.Antialiasing == "off" ? Viewport.Msaa.Disabled : preferences.MsaaSamples switch {
                2 => Viewport.Msaa.Msaa2X, 8 => Viewport.Msaa.Msaa8X, _ => Viewport.Msaa.Msaa4X };
        }
        backdrop.SetVisible(!worldVisible);
        world.SetPresentationBounds(ui.WorldPresentationBounds);
        world.Sync(core, delta, worldVisible);
        combatEffects.Sync(core,new CombatPresentationFrame(world.PresentedShipRoot,core.CurrentPresentedShip?.Id??"",world.RenderOriginX,world.RenderOriginY,
            world.StandingElevation,world.IsInterior,ui.WorldVisible&&!worldPreview&&(core.Instance!=null||core.PassengerInterior!=null||core.Eva!=null),1,preferences.ReducedMotion,WorldRoot:world),
            Time.GetTicksMsec()/1000.0,world.CombatBody);
        ui.SetGroundItemLabels(world.GroundItemLabels(core).Select(item =>
            (item.Id, item.DefinitionId, item.Reachable, item.Position)).ToArray());
        ui.SetGraphicsStatus(world.GraphicsStatus);
        if (uiSmoke && !smokePresentationApplied && ui.WorldVisible && core.SpatialReady)
        {
            if (smokeFlightView) core.SetInteriorView(false);
            var body = world.Space.AuthorizedBodies.FirstOrDefault(b =>
                smokeObserveAppearance != null ? b.Appearance == smokeObserveAppearance : smokeObserveKind != null && b.Kind == smokeObserveKind);
            if (body.Id != null) world.ObserveBody(body.Id);
            if (smokeObserveKind == null && smokeObserveAppearance == null || body.Id != null) smokePresentationApplied = true;
        }
        if (smokeDocumentPath != null && core.Instance != null)
        {
            File.WriteAllText(smokeDocumentPath, core.Instance.DocumentJson);
            smokeDocumentPath = null;
        }
        if (worldVisible && !worldPreview && !world.GameplayReady)
            ui.Refresh(authTask != null, world.Status);
        var keyboardAllowed = focused && worldVisible && !ui.BlocksKeyboardInput && world.GameplayReady;
        var pointerAllowed = focused && worldVisible && !ui.BlocksPointerInput;
        if (!keyboardAllowed)
        {
            gameplayKeys.Clear();
            if (keyboardWasAllowed) core.StopMovementForUiCapture();
        }
        keyboardWasAllowed = keyboardAllowed;
        if (!pointerAllowed) { core.SetAimAngle(null); core.SetPointerDirection(null, null); core.SetTrigger(false); }
        else UpdatePointerAim();
        core.TickGameplay(delta, keyboardAllowed, pointerAllowed, allowUiActions: focused);
        if (gameplayEpoch != core.GameplayEpoch) { gameplayEpoch = core.GameplayEpoch; gameplayKeys.Clear(); orbiting = false; }
        var horizontal = (gameplayKeys.IsPressed("D") ? 1 : 0) - (gameplayKeys.IsPressed("A") ? 1 : 0);
        var forward = (gameplayKeys.IsPressed("W") ? 1 : 0) - (gameplayKeys.IsPressed("S") ? 1 : 0);
        var mapped = GameplayRules.ScreenToDeck(horizontal, forward, world.InputCameraAlpha, core.CurrentPresentedShip?.Heading ?? 0);
        core.SendGameplayIntent(forward, horizontal, mapped.Dx, mapped.Dy, gameplayKeys.IsPressed("Shift"));
        if (capturePath != null && ++frames == captureAfter)
        {
            GetViewport().GetTexture().GetImage().SavePng(capturePath);
            GetTree().Quit();
        }
        if (smokeStatePath != null && (smokeStateClock += delta) >= 0.1)
        {
            smokeStateClock = 0;
            var state = JsonSerializer.Serialize(new { ready = ui.WorldVisible, blocked = ui.BlocksWorldInput, focused, controls = core.ControlsClaimed, x = core.Character?.LocalX, y = core.Character?.LocalY,
                key = smokeLastKey, ui = ui.SmokeFacts(), world = new { world.Status, world.LoadedAssetCount, world.RenderedPlacementCount, missing = world.MissingAssetIds,
                    projected = new { x = world.ProjectedShipBounds.Position.X, y = world.ProjectedShipBounds.Position.Y, width = world.ProjectedShipBounds.Size.X, height = world.ProjectedShipBounds.Size.Y } },
                space = world.SpaceFacts, preferences = NativePreferences.Current.Snapshot,
                effects = new {combatEffects.AcceptedShots,combatEffects.AcceptedImpacts,combatEffects.LiveCount,combatEffects.PendingCount,missing=combatEffects.MissingAssets},
                gameplay = new { core.GameplayEpoch, core.SharedWorldEpoch, core.SpatialReady, core.SpatialCellSets, core.InteriorView,
                    core.CombatEnabled, core.CruiseActive, core.GameplayPending, core.GameplayMessage,
                    interaction = core.Interaction, selectedPlacement = core.SelectedPlacementId },
                instance = core.Instance == null ? null : new { core.Instance.BlueprintId, core.Instance.BlueprintSha256, core.Instance.Revision, core.Instance.FurnishingRevision } });
            File.WriteAllText(smokeStatePath + ".tmp", state); File.Move(smokeStatePath + ".tmp", smokeStatePath, true);
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit &&
            key.PhysicalKeycode is not (Key.Escape or Key.F3 or Key.F6)) return;
        if (uiSmoke) smokeLastKey = key.PhysicalKeycode.ToString();
        if (ui.HandleKey(key.PhysicalKeycode)) { GetViewport().SetInputAsHandled(); return; }
        if (key.PhysicalKeycode == Key.Escape) { ClearGameplayInput(); return; }
        if (ui.BlocksKeyboardInput || !world.GameplayReady) return;
        GameplayShortcut(key.PhysicalKeycode);
    }

    private void GameplayShortcut(Key key)
    {
        if (!focused || !ui.WorldVisible) return;
        switch (key)
        {
            case Key.Tab: ClearGameplayInput(); core.ToggleView(); break;
            case Key.E: core.Interact(); break;
            case Key.X: core.ToggleCruiseOrSuit(); break;
            case Key.V: core.ToggleCombat(); break;
            case Key.R: core.ReloadWeapon(); break;
            case Key.B: core.RescueBeacon(); break;
            case Key.W: case Key.A: case Key.S: case Key.D:
                if (gameplayKeys.Press(key.ToString(), !ui.BlocksKeyboardInput)) core.ClaimControls();
                break;
            case Key.Shift: gameplayKeys.Press("Shift", !ui.BlocksKeyboardInput); break;
        }
    }

    public override void _Input(InputEvent input)
    {
        if (input is InputEventKey { Pressed: false } key) gameplayKeys.Release(key.PhysicalKeycode.ToString());
        if (input is InputEventMouseButton { Pressed: false } button)
        {
            if (button.ButtonIndex == MouseButton.Right) orbiting = false;
            if (button.ButtonIndex == MouseButton.Left) core.SetTrigger(false);
        }
        if (input is InputEventMouseMotion motion && orbiting)
        {
            if (!focused || ui.BlocksCameraInput) { orbiting = false; return; }
            world.Orbit(motion.Relative); GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if (!focused || !ui.WorldVisible || input is not InputEventMouseButton { Pressed: true } button) return;
        if (button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown && !ui.BlocksCameraInput)
        { world.Zoom(button.ButtonIndex == MouseButton.WheelUp ? -100 : 100); GetViewport().SetInputAsHandled(); }
        else if (button.ButtonIndex == MouseButton.Right && !ui.BlocksCameraInput)
        { orbiting = world.ShouldOrbit; GetViewport().SetInputAsHandled(); }
        else if (button.ButtonIndex == MouseButton.Left && !ui.BlocksPointerInput)
        {
            GetViewport().GuiReleaseFocus();
            UpdatePointerAim();
            if (core.CombatEnabled) core.SetTrigger(true);
            else ui.ShowObjectDetails(world.Pick(button.Position)?.PlacementId);
            GetViewport().SetInputAsHandled();
        }
    }

    private void UpdatePointerAim()
    {
        var local = world.ScreenToDeckPoint(GetViewport().GetMousePosition());
        if (local == null || core.Character == null) { core.SetAimAngle(null); core.SetPointerDirection(null, null); return; }
        var actorX = world.ActorInputPosition.X;
        var actorY = world.ActorInputPosition.Y;
        var dx = local.Value.X - actorX; var dy = local.Value.Y - actorY;
        core.SetPointerDirection(dx, dy);
        core.SetAimAngle(dx * dx + dy * dy > 1e-12 ? Math.Atan2(dx, dy) : null);
    }

    private void ClearGameplayInput()
    { gameplayKeys.Clear(); orbiting = false; core.CancelGameplayInput(); }

    public override void _ExitTree() { authCancel.Cancel(); core.Dispose(); authCancel.Dispose(); }
}
