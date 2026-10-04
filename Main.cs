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
    private Node3D ship = null!, deck = null!;
    private MeshInstance3D actor = null!;
    private Camera3D camera = null!;
    private string? layoutKey;
    private bool focused = true, signingOut;
    private bool refreshing;
    private DateTimeOffset refreshRetryAt;
    private string? authMessage;
    private double intentClock;
    private string? capturePath;
    private int frames;
    private int captureAfter = 100;
    private bool uiSmoke, smokeEntered;
    private string? smokeStatePath;
    private double smokeStateClock;
    private string? smokeLastKey;

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        var settingsPath = args.FirstOrDefault(a => a.StartsWith("--settings="))?.Split('=', 2)[1];
        var json = settingsPath == null ? Godot.FileAccess.GetFileAsString("res://client-settings.json") : File.ReadAllText(settingsPath);
        var settings = ClientSettings.Parse(json);
        uiSmoke = args.Contains("--ui-smoke");
        if (uiSmoke && (settingsPath == null || settings.GameOrigin != "http://127.0.0.1:3131" || !settings.Database.EndsWith("-smoke", StringComparison.Ordinal)))
            throw new InvalidOperationException("UI smoke requires an explicit settings file targeting the isolated loopback smoke database.");
        smokeStatePath = uiSmoke ? args.FirstOrDefault(a => a.StartsWith("--ui-smoke-state="))?.Split('=', 2)[1] : null;
        if (args.Contains("--ui-demo")) smokeStatePath ??= args.FirstOrDefault(a => a.StartsWith("--ui-demo-state="))?.Split('=', 2)[1];
        core = new ClientCore(settings);
        auth = new NativeAuth(settings);
        capturePath = args.FirstOrDefault(a => a.StartsWith("--capture="))?.Split('=', 2)[1];
        var captureFrame = args.FirstOrDefault(a => a.StartsWith("--capture-after="))?.Split('=', 2)[1];
        if (int.TryParse(captureFrame, out var requestedFrame)) captureAfter = Math.Clamp(requestedFrame, 1, 20000);
        BuildWorld();
        var layer = new CanvasLayer(); AddChild(layer);
        ui = new SiderealUi(core, StartSignIn, SignOut, args.Contains("--ui-demo")); layer.AddChild(ui);
        if (uiSmoke) core.Connect("", provider: false);
        GetWindow().FocusExited += () => { focused = false; core.ReleaseControls(); };
        GetWindow().FocusEntered += () => focused = true;
    }

    private void BuildWorld()
    {
        var world = new WorldEnvironment { Environment = new Godot.Environment {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color("07121e"),
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color("a0bbcc"), AmbientLightEnergy = 0.7f
        } };
        AddChild(world);
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55, -35, 0), LightEnergy = 1.4f, LightCullMask = 1u });
        ship = new Node3D(); AddChild(ship);
        deck = new Node3D(); ship.AddChild(deck);
        actor = new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.27f, Height = 1.5f }, MaterialOverride = Material("71d0c1") };
        ship.AddChild(actor); actor.Visible = false;
        camera = new Camera3D { Position = new Vector3(0, 21, 21), Current = true, Projection = Camera3D.ProjectionType.Orthogonal, Size = 26, CullMask = 1u };
        AddChild(camera); camera.LookAt(Vector3.Zero, Vector3.Up);
        backdrop = new FrontendBackdrop(); AddChild(backdrop);
    }

    private static StandardMaterial3D Material(string hex) => new() { AlbedoColor = new Color(hex), Roughness = 0.82f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
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
        backdrop.SetVisible(!ui.WorldVisible);
        if (ui.WorldVisible) camera.Current = true;
        var character = core.Character;
        if (character != null)
        {
            RenderState();
        }
        else
        {
            actor.Visible = false;
            ClearDeck(); layoutKey = null;
        }
        intentClock += delta;
        if (intentClock >= 0.05)
        {
            intentClock = 0;
            if (!focused || ui.BlocksWorldInput) core.ReleaseControls();
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
                key = smokeLastKey, ui = ui.SmokeFacts() });
            File.WriteAllText(smokeStatePath + ".tmp", state); File.Move(smokeStatePath + ".tmp", smokeStatePath, true);
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key || GetViewport().GuiGetFocusOwner() is LineEdit) return;
        if (uiSmoke) smokeLastKey = key.PhysicalKeycode.ToString();
        if (ui.HandleKey(key.PhysicalKeycode)) { GetViewport().SetInputAsHandled(); return; }
        if (key.PhysicalKeycode == Key.Escape) core.ReleaseControls();
        else if (ui.BlocksWorldInput) return;
        else if (key.PhysicalKeycode == Key.E) core.ToggleSeat();
        else if (key.PhysicalKeycode is Key.W or Key.A or Key.S or Key.D) core.ClaimControls();
    }

    private void ClearDeck() { foreach (var node in deck.GetChildren()) { deck.RemoveChild(node); node.QueueFree(); } }
    private void RenderState()
    {
        var character = core.Character!;
        actor.Visible = true;
        var local = RelativePose.World(character.LocalX, character.LocalY, 0, 0, (core.Location?.StandingElevationM ?? 0) + 0.75);
        actor.Position = new Vector3((float)local.X, (float)local.Height, (float)local.Z);
        ship.Rotation = new Vector3(0, (float)(core.Ship?.Heading ?? 0), 0);
        var target = actor.GlobalPosition;
        camera.Position = camera.Position.Lerp(target + new Vector3(0, 21, 21), 0.08f); camera.LookAt(target, Vector3.Up);
        var instance = core.Instance;
        var key = instance == null ? null : $"{instance.Id}:{instance.Revision}:{core.Location?.DeckId}";
        if (layoutKey == key) return;
        ClearDeck(); layoutKey = key;
        if (instance == null) return;
        try
        {
            using var doc = JsonDocument.Parse(instance.DocumentJson);
            var layout = doc.RootElement.GetProperty("layout");
            var deckHeight = layout.GetProperty("decks").EnumerateArray().First(d => d.GetProperty("id").GetString() == core.Location?.DeckId).GetProperty("elevation").GetSingle() / 32;
            foreach (var tile in layout.GetProperty("tiles").EnumerateArray())
            {
                if (tile.GetProperty("deckId").GetString() != core.Location?.DeckId) continue;
                var vertices = tile.GetProperty("vertices").EnumerateArray().Select(p => new Vector2(p[0].GetSingle() / 32, -p[1].GetSingle() / 32)).ToArray();
                var indices = Geometry2D.TriangulatePolygon(vertices);
                var surface = new SurfaceTool(); surface.Begin(Mesh.PrimitiveType.Triangles);
                foreach (var index in indices) { surface.SetNormal(Vector3.Up); surface.AddVertex(new Vector3(vertices[index].X, deckHeight, vertices[index].Y)); }
                deck.AddChild(new MeshInstance3D { Mesh = surface.Commit(), MaterialOverride = Material("345360") });
            }
            foreach (var wall in layout.GetProperty("partitions").EnumerateArray())
            {
                if (wall.GetProperty("deckId").GetString() != core.Location?.DeckId) continue;
                var a = wall.GetProperty("a"); var b = wall.GetProperty("b");
                var start = new Vector3(a[0].GetSingle() / 32, deckHeight + 0.3f, -a[1].GetSingle() / 32);
                var end = new Vector3(b[0].GetSingle() / 32, deckHeight + 0.3f, -b[1].GetSingle() / 32);
                var mesh = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.12f, 0.6f, start.DistanceTo(end)) }, Position = (start + end) / 2, MaterialOverride = Material("a0b4bf") };
                deck.AddChild(mesh); mesh.LookAt(end, Vector3.Up);
            }
        }
        catch { ui.ShowMessage("This ship layout needs a newer native renderer. Your server state is unchanged."); }
    }

    public override void _ExitTree() { authCancel.Cancel(); core.Dispose(); authCancel.Dispose(); }
}
