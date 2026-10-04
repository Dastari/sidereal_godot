using Godot;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sidereal.Native;
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
    private bool focused = true, signingOut;
    private bool refreshing;
    private DateTimeOffset refreshRetryAt;
    private string? authMessage;
    private double intentClock;
    private string? capturePath;
    private int frames;
    private int captureAfter = 100;
    private bool uiSmoke, smokeEntered, worldPreview;
    private string? smokeStatePath;
    private string? smokeDocumentPath;
    private double smokeStateClock;
    private string? smokeLastKey;

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        var settingsPath = args.FirstOrDefault(a => a.StartsWith("--settings="))?.Split('=', 2)[1];
        var json = settingsPath == null ? Godot.FileAccess.GetFileAsString("res://client-settings.json") : File.ReadAllText(settingsPath);
        var settings = ClientSettings.Parse(json);
        uiSmoke = args.Contains("--ui-smoke");
        worldPreview = args.Contains("--world-preview");
        if (uiSmoke && (settingsPath == null || !settings.IsIsolatedFixture))
            throw new InvalidOperationException("UI smoke requires explicit settings for the isolated local or Tailscale test database.");
        smokeStatePath = uiSmoke ? args.FirstOrDefault(a => a.StartsWith("--ui-smoke-state="))?.Split('=', 2)[1] : null;
        smokeDocumentPath = uiSmoke ? args.FirstOrDefault(a => a.StartsWith("--ui-smoke-document="))?.Split('=', 2)[1] : null;
        if (args.Contains("--ui-demo")) smokeStatePath ??= args.FirstOrDefault(a => a.StartsWith("--ui-demo-state="))?.Split('=', 2)[1];
        if (worldPreview) smokeStatePath ??= args.FirstOrDefault(a => a.StartsWith("--world-preview-state="))?.Split('=', 2)[1];
        core = new ClientCore(settings);
        auth = new NativeAuth(settings);
        capturePath = args.FirstOrDefault(a => a.StartsWith("--capture="))?.Split('=', 2)[1];
        var captureFrame = args.FirstOrDefault(a => a.StartsWith("--capture-after="))?.Split('=', 2)[1];
        if (int.TryParse(captureFrame, out var requestedFrame)) captureAfter = Math.Clamp(requestedFrame, 1, 20000);
        BuildWorld();
        world.SetPreview(worldPreview);
        var layer = new CanvasLayer(); AddChild(layer);
        ui = new SiderealUi(core, StartSignIn, SignOut, args.Contains("--ui-demo")); layer.AddChild(ui);
        if (uiSmoke) core.Connect("", provider: false);
        GetWindow().FocusExited += () => { focused = false; core.ReleaseControls(); };
        GetWindow().FocusEntered += () => focused = true;
    }

    private void BuildWorld()
    {
        world = new ReplicatedWorld(); AddChild(world);
        backdrop = new FrontendBackdrop(); AddChild(backdrop);
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
        if (uiSmoke && !smokeEntered && core.Connection != null) { smokeEntered = true; ui.RequestEnter("UI Smoke Crew"); }
        ui.Refresh(authTask != null, authTask != null ? (refreshing ? "Renewing game session…" : "Complete sign-in in your browser, then return here.") : authMessage ?? core.Status);
        var worldVisible = ui.WorldVisible || worldPreview;
        backdrop.SetVisible(!worldVisible);
        world.SetPresentationBounds(ui.WorldPresentationBounds);
        world.Sync(core, delta, worldVisible);
        if (smokeDocumentPath != null && core.Instance != null)
        {
            File.WriteAllText(smokeDocumentPath, core.Instance.DocumentJson);
            smokeDocumentPath = null;
        }
        if (worldVisible && !worldPreview && (world.RenderedPlacementCount == 0 || world.MissingAssetIds.Length != 0))
            ui.Refresh(authTask != null, world.Status);
        intentClock += delta;
        if (intentClock >= 0.05)
        {
            intentClock = 0;
            if (!focused || ui.BlocksWorldInput || world.RenderedPlacementCount == 0 || world.MissingAssetIds.Length != 0) core.ReleaseControls();
            else if (core.ControlsClaimed)
            {
                var x = (Input.IsPhysicalKeyPressed(Key.D) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.A) ? 1 : 0);
                var y = (Input.IsPhysicalKeyPressed(Key.W) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.S) ? 1 : 0);
                core.SendIntent(core.IsPiloting ? y : 0, core.IsPiloting ? -x : 0, core.IsPiloting ? 0 : x, core.IsPiloting ? 0 : y, Input.IsPhysicalKeyPressed(Key.Shift));
            }
        }
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
                instance = core.Instance == null ? null : new { core.Instance.BlueprintId, core.Instance.BlueprintSha256, core.Instance.Revision, core.Instance.FurnishingRevision } });
            File.WriteAllText(smokeStatePath + ".tmp", state); File.Move(smokeStatePath + ".tmp", smokeStatePath, true);
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key || GetViewport().GuiGetFocusOwner() is LineEdit) return;
        if (uiSmoke) smokeLastKey = key.PhysicalKeycode.ToString();
        if (ui.HandleKey(key.PhysicalKeycode)) { GetViewport().SetInputAsHandled(); return; }
        if (key.PhysicalKeycode == Key.Escape) core.ReleaseControls();
        else if (ui.BlocksWorldInput || world.RenderedPlacementCount == 0 || world.MissingAssetIds.Length != 0) return;
        else if (key.PhysicalKeycode == Key.E) core.ToggleSeat();
        else if (key.PhysicalKeycode is Key.W or Key.A or Key.S or Key.D) core.ClaimControls();
    }

    public override void _ExitTree() { authCancel.Cancel(); core.Dispose(); authCancel.Dispose(); }
}
