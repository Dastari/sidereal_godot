using Godot;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Sidereal.Native;
using Sidereal.Native.Input;

/// <summary>Own authorized interior plus public space presentation. No simulation authority.</summary>
public partial class ReplicatedWorld : Node3D
{
    private const uint WorldLayer = 1u;
    private static ReplicatedWorldCatalog? catalog;
    public static ReplicatedWorldCatalog Catalog => catalog ??= new ReplicatedWorldCatalog(FileAccess.GetFileAsString("res://Assets/World/manifest.json"));
    private Node3D shipRoot = null!;
    private ReplicatedWorldAssembly? assembly;
    private Node3D characterMarker = null!;
    private SpaceEnvironment space = null!;
    private SpaceRemoteShips remote = null!;
    private SpaceExhaust ownExhaust = null!;
    private CrewPresenter crew = null!;
    private SpaceGroundItems ground = null!;
    private SourceLightUnitsRig lighting = null!;
    private readonly OwnedMotionPresentation ownedMotion = new();
    private OwnedMotionFrame? ownedDisplay;
    private ulong ownedGeneration;
    private bool presentationDeclined;
    private string? layoutKey;
    private bool preview, framed, interior = true, cameraChanged;
    private Rect2 requestedPresentationBounds;
    private double orbit = .45, alpha = .45, beta = SpaceMath.DeckBeta, deckZoom = 12, flightZoom = 55, shownDeckZoom = 12, shownFlightZoom = 55, blend = 1;
    private double initialDeckZoom=12,initialFlightZoom=55;
    private string? cameraFrameKey;
    private double observeAlpha = .45, observeBeta = SpaceMath.DeckBeta, observeRatio = 5, shownObserveRatio = 5;
    private double elapsed, standingElevation = .1875;
    private bool evaShown;
    private double? beforeEvaZoom;
    private NativePreferencesSnapshot preferences = new();
    private readonly Dictionary<string,string> placementIdentities = new();
    public Rect2 PresentationBounds { get; private set; }
    public Rect2 ProjectedShipBounds { get; private set; }
    public Camera3D Camera { get; private set; } = null!;
    public double RenderOriginX { get; private set; }
    public double RenderOriginY { get; private set; }
    public double InputCameraAlpha => alpha;
    public double CurrentCameraAlpha => alpha;
    public bool IsInterior => interior;
    public string? ObservedBodyId { get; private set; }
    public bool ShouldOrbit => interior || ObservedBodyId != null;
    public string Status { get; private set; } = "Awaiting your ship.";
    public int LoadedAssetCount => assembly?.LoadedAssetCount ?? 0;
    public int RenderedPlacementCount => assembly?.RenderedPlacementCount ?? 0;
    public string[] MissingAssetIds => (assembly?.MissingAssetIds ?? Array.Empty<string>()).Concat(space?.MissingAssetPins ?? Array.Empty<string>()).Concat(remote?.MissingAssetPins??Array.Empty<string>()).Concat(crew?.MissingAssets??Array.Empty<string>()).Concat(ground?.MissingAssetPins??Array.Empty<string>()).Distinct().ToArray();
    public string? PrefabId => assembly?.PrefabId;
    public bool IsPreview => preview;
    public bool GameplayReady { get; private set; }
    public Vector2 ActorInputPosition { get; private set; }
    public Node3D PresentedShipRoot => shipRoot;
    public double StandingElevation => standingElevation;
    public double PresentationElapsed => elapsed;
    public string[] VistaIds => space?.VistaIds ?? Array.Empty<string>();
    public string GraphicsStatus => $"{RenderingServer.GetCurrentRenderingMethod()} / {RenderingServer.GetCurrentRenderingDriverName()} · {(preferences.Antialiasing == "off" ? "AA off" : $"MSAA {preferences.MsaaSamples}×")} · {preferences.RenderScale:P0} render scale · authored sockets {assembly?.EffectiveLocalLights??0}/{(RenderingServer.GetCurrentRenderingMethod()=="gl_compatibility"?8:32)} active · source keys/rims 7/{SourceLightUnits.DirectionalLimit} · glow {(Camera.Environment?.GlowEnabled==true?"on":"off")}";
    public SpaceEnvironment Space => space;
    public CombatBodyAnchor? CombatBody(string id)=>crew.CombatAnchor(id);
    public object SpaceFacts => new { bodies = space.RenderedBodyCount, retainedBodies = space.RetainedBodyCount, asteroids = space.AsteroidCount, stars = 8192, remoteShips = remote.RepresentedCount, fullRemoteShips = remote.FullCount, ownJets = ownExhaust.LitCount, readyCrew = crew.ReadyCount, pendingCrew = crew.PendingCount, unsupportedCrew = crew.UnsupportedCount, observedBody = ObservedBodyId, interior, vista = space.ActiveVista,skyDisplayStatus=space.SkyDisplayStatus,skyDisplayProfile=space.SkyDisplayProfile.ToString(), cameraAlpha = alpha, cameraBeta = beta, initialDeckHalfExtent=initialDeckZoom, initialFlightHalfExtent=initialFlightZoom, displayedDeckHalfExtent=shownDeckZoom, displayedFlightHalfExtent=shownFlightZoom, frameCenterX=assembly?.CameraFrame.CenterX, frameCenterY=assembly?.CameraFrame.CenterY, fovRadians = SpaceMath.FieldOfView, originX = RenderOriginX, originY = RenderOriginY, cameraNear=Camera.Near, cameraFar=Camera.Far, renderer=RenderingServer.GetCurrentRenderingMethod(), driver=RenderingServer.GetCurrentRenderingDriverName(), glow=Camera.Environment?.GlowEnabled, ownedMotion=ownedDisplay,ownedYaw=crew.OwnYaw,lighting=lighting.Facts, authoredSockets=assembly?.EffectiveLocalLights??0,nativePracticalNodes=assembly?.EffectiveNativeLocalLights??0 };

    public override void _Ready()
    {
        Name = "ReplicatedWorld"; shipRoot = new Node3D { Name = "ServerShipFrame" }; AddChild(shipRoot);
        var sky = new Sky { SkyMaterial = new PanoramaSkyMaterial { Panorama = GD.Load<Texture2D>(SpaceEnvironment.AssetRoot + "molded-studio.hdr") } };
        Camera = new Camera3D { Name = "GameplayCamera", Projection = Camera3D.ProjectionType.Perspective, Fov = (float)(SpaceMath.FieldOfView * 180 / Math.PI), KeepAspect = Camera3D.KeepAspectEnum.Height,
            CullMask = SourceLightUnits.CameraMask(WorldLayer), Near = .1f, Far = 1600,
            Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color("02050a"), AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(.84f,.9f,1), AmbientLightEnergy = .35f, ReflectedLightSource = Godot.Environment.ReflectionSource.Sky, Sky = sky,
                TonemapMode = Godot.Environment.ToneMapper.Filmic, TonemapExposure = .97f } };
        AddChild(Camera);
        lighting = new SourceLightUnitsRig(WorldLayer); AddChild(lighting);
        space = new SpaceEnvironment(WorldLayer); AddChild(space);
        remote = new SpaceRemoteShips(WorldLayer); AddChild(remote);
        ownExhaust = new SpaceExhaust(WorldLayer); shipRoot.AddChild(ownExhaust);
        crew = new CrewPresenter { Name = "AuthorizedCrew" }; AddChild(crew);
        ground = new SpaceGroundItems { Name = "AuthorizedGroundItems" }; AddChild(ground);
        characterMarker = new Node3D { Name = "CharacterPositionMarker", Visible = false };
        characterMarker.AddChild(new MeshInstance3D { Name = "PositionRing", Mesh = new TorusMesh { InnerRadius = .24f, OuterRadius = .29f, Rings = 20, RingSegments = 6 }, Layers = WorldLayer,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("39daf3"), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } }); shipRoot.AddChild(characterMarker);
        ApplyPreferences(NativePreferences.Current.Snapshot);
    }
    public void ApplyPreferences(NativePreferencesSnapshot next)
    {
        preferences = next.Normalized(); if (Camera == null) return;
        GetViewport().Msaa3D = preferences.Antialiasing == "off" ? Viewport.Msaa.Disabled : preferences.MsaaSamples switch { 2 => Viewport.Msaa.Msaa2X, 8 => Viewport.Msaa.Msaa8X, _ => Viewport.Msaa.Msaa4X };
        GetViewport().Scaling3DScale = (float)preferences.RenderScale;
        lighting.Sync(new Vector3(-.6f,-1,.45f),-Camera.GlobalBasis.Z,preferences.Lighting,preferences.Shadows,Colors.White);
        Camera.Environment!.AmbientLightEnergy = preferences.Lighting ? .35f : 0;
        Camera.Environment.ReflectedLightSource = preferences.Lighting ? Godot.Environment.ReflectionSource.Sky : Godot.Environment.ReflectionSource.Disabled;
        Camera.Environment.GlowEnabled = preferences.Glow;
        Camera.Environment.GlowIntensity = .45f;
        assembly?.ApplyPreferences(preferences);
        space?.ApplyPreferences(preferences);
    }
    public void SetPreview(bool enabled) { if (preview == enabled) return; preview = enabled; ClearAssembly(); }
    public void SetPresentationBounds(Rect2 bounds) => requestedPresentationBounds = bounds;
    public void SetViewMode(bool deck) { interior = deck; ObservedBodyId = null; cameraChanged = true; }
    public void ToggleViewMode() => SetViewMode(!interior);
    public void ResetCamera() { orbit = .45; deckZoom = shownDeckZoom = initialDeckZoom; flightZoom = shownFlightZoom = initialFlightZoom; observeRatio = shownObserveRatio = 5; observeAlpha = .45; observeBeta = SpaceMath.DeckBeta; ObservedBodyId = null; cameraChanged = true; }
    public void ObserveBody(string? id) { if (id != null && !space.AuthorizedBodies.Any(b => b.Id == id && b.Kind is "planet" or "star")) return; ObservedBodyId = id; observeAlpha = .45; observeBeta = SpaceMath.DeckBeta; observeRatio = shownObserveRatio = 5; cameraChanged = true; }
    public void Zoom(double wheelPixels)
    {
        if (!double.IsFinite(wheelPixels)) return; var d = Math.Clamp(wheelPixels,-300,300);
        if (ObservedBodyId != null) observeRatio = Math.Clamp(observeRatio * Math.Exp(d * .002),1.6,24);
        else if (interior) deckZoom = Math.Clamp(deckZoom * Math.Exp(d * .001),2,18);
        else flightZoom = Math.Clamp(flightZoom * Math.Exp(d * .002),2,650);
    }
    public void Orbit(Vector2 delta)
    {
        if (!delta.IsFinite()) return;
        if (ObservedBodyId != null) { observeAlpha -= delta.X * .007; observeBeta = Math.Clamp(observeBeta - delta.Y * .005,.12,Math.PI-.12); }
        else if (interior) orbit -= delta.X * .007;
    }
    public Vector2? ScreenToDeckPoint(Vector2 physical)
    {
        if (presentationDeclined || !Visible || assembly == null || !physical.IsFinite()) return null;
        var ray = Camera.ProjectRayNormal(physical); var origin = Camera.ProjectRayOrigin(physical); if (Math.Abs(ray.Y) < 1e-6) return null;
        var distance = ((float)standingElevation - origin.Y) / ray.Y; if (distance < 0 || distance > 10000) return null;
        var local = shipRoot.GlobalTransform.AffineInverse() * (origin + ray * distance); return new Vector2(local.X,-local.Z);
    }
    public ReplicatedWorldAssembly.Hit? Pick(Vector2 physical)
    {
        if(presentationDeclined||!Visible||assembly==null||!physical.IsFinite())return null;
        var inverse=shipRoot.GlobalTransform.AffineInverse();var hit=assembly.Pick(inverse*Camera.ProjectRayOrigin(physical),inverse.Basis*Camera.ProjectRayNormal(physical));
        return hit is { } value&&placementIdentities.TryGetValue(value.PlacementId,out var identity)?value with {PlacementId=identity}:hit;
    }
    public readonly record struct GroundLabel(string Id,string DefinitionId,bool Reachable,Vector2 Position);
    public GroundLabel[] GroundItemLabels(ClientCore core)
    {
        if(presentationDeclined||!Visible||preview)return Array.Empty<GroundLabel>();
        var viewport=GetViewport().GetVisibleRect();
        return SpaceGroundItems.ReadRows(core,interior).Select(r=>
        {
            var point=shipRoot.GlobalTransform*new Vector3((float)r.LocalX,(float)r.ElevationM+.75f,-(float)r.LocalY);
            var distance=(point-Camera.GlobalPosition).Dot(-Camera.GlobalBasis.Z);
            return (Row:r,Position:Camera.UnprojectPosition(point),Visible:distance>=Camera.Near&&distance<=Camera.Far);
        }).Where(r=>r.Visible&&viewport.HasPoint(r.Position)).Select(r=>new GroundLabel(r.Row.Id,r.Row.DefinitionId,r.Row.Reachable,r.Position)).ToArray();
    }
    private void ClearAssembly()
    {
        if (assembly != null) { shipRoot.RemoveChild(assembly); assembly.QueueFree(); assembly = null; }
        layoutKey = null; cameraFrameKey=null; framed = false; ProjectedShipBounds = default;placementIdentities.Clear();
        if (characterMarker != null) characterMarker.Visible = false;
        ownExhaust?.Clear(); space?.Clear(); remote?.Clear(); crew?.Clear(); ground?.Clear(); ObservedBodyId = null;
    }
    public static ReplicatedWorldAssembly CreatePreviewAssembly(uint layers = WorldLayer, bool cutaway = true, string previewPrefabId = "fed.m.wayfarer") => new(Catalog.Preview(previewPrefabId),"{}",layers,cutaway);
    public static ReplicatedWorldAssembly CreateExteriorAssembly(string prefabId, ulong revision, uint layers = WorldLayer) => new(Catalog.Exterior(prefabId,revision),"{}",layers,false,true);
    public static ReplicatedWorldAssembly BuildAssembly(string documentJson, string furnishingsJson, string? deckId, uint layers = WorldLayer, bool cutaway = true) => new(Catalog.Match(documentJson,deckId),furnishingsJson,layers,cutaway);
    public void Sync(ClientCore core, double delta, bool visible)
    {
        Visible = visible; Camera.Current = visible;presentationDeclined=false;
        GameplayReady=!preview&&core.Connection!=null&&core.Character!=null&&core.Alive&&(core.Location!=null||core.Eva!=null);
        if (!preview && core.Connection == null && layoutKey!=null) { ClearAssembly(); Status = "Awaiting your replicated ship."; }
        if (!visible) { ownedMotion.Clear();ownedDisplay=null;return; } elapsed += preferences.ReducedMotion ? 0 : Math.Max(0,delta);
        interior = preview || core.InteriorView&&core.Eva==null;
        var exterior=core.SpatialReady?core.Connection?.Db.VisibleShipDescriptions.Iter().FirstOrDefault(d=>d.ShipId==core.CurrentPresentedShip?.Id):null;
        var publicPrefab=exterior?.PublishedExteriorAssetId is { } asset&&asset.StartsWith("prefab:",StringComparison.Ordinal)?asset[7..]:null;
        var instance = core.Instance; var next = preview ? "published-wayfarer-preview" : instance == null ? publicPrefab==null?null:$"public:{core.CurrentPresentedShip?.Id}:{publicPrefab}:{exterior!.AppearanceRevision}:{core.SharedWorldEpoch}" : $"{instance.Id}:{instance.Revision}:{instance.FurnishingRevision}:{core.Location?.DeckId}";
        if (next != layoutKey)
        {
            layoutKey = next; framed = false; ownExhaust.Clear();placementIdentities.Clear(); if (assembly != null) { shipRoot.RemoveChild(assembly); assembly.QueueFree(); assembly = null; }
            try {
                assembly = preview ? CreatePreviewAssembly() : instance == null ? publicPrefab==null?null:CreateExteriorAssembly(publicPrefab,exterior!.AppearanceRevision) : BuildAssembly(instance.DocumentJson,instance.FurnishingsJson,core.Location?.DeckId);
                if(instance!=null){using var document=JsonDocument.Parse(instance.DocumentJson);if(document.RootElement.GetProperty("prefab").TryGetProperty("identities",out var identities))foreach(var id in identities.EnumerateObject())placementIdentities[id.Name]=id.Value.GetString()!;}
                if (assembly != null) {
                    shipRoot.AddChild(assembly); assembly.ApplyPreferences(preferences);
                    // The native adapter admits one deck only. Public exterior and
                    // private interior views of that same pin preserve wheel intent.
                    var frameKey=$"{(preview?"preview":core.ActiveShipId)}:{assembly.PrefabId}:{assembly.PrefabRevision}";
                    if(cameraFrameKey!=frameKey){cameraFrameKey=frameKey;var frame=assembly.CameraFrame;initialDeckZoom=frame.InitialDeckZoom;initialFlightZoom=frame.InitialFlightZoom;deckZoom=shownDeckZoom=initialDeckZoom;flightZoom=shownFlightZoom=initialFlightZoom;cameraChanged=true;}
                }
                Status = assembly == null ? "Awaiting your replicated ship." : preview ? "Published Wayfarer preview · local presentation only" : "Authored ship · server-confirmed layout and furnishings";
            } catch(Exception error) { Status = error is InvalidOperationException ? error.Message : "This ship needs an updated native asset renderer."; GD.PushWarning($"Native ship assembly unavailable ({error.GetType().Name})."); }
        }
        var vessel = core.CurrentPresentedShip;
        if((core.Eva!=null)!=evaShown){evaShown=core.Eva!=null;if(evaShown){beforeEvaZoom=flightZoom;flightZoom=7;}else if(beforeEvaZoom is { } restored){flightZoom=restored;beforeEvaZoom=null;}}
        var authorizedBodies = SpaceEnvironment.ReadBodies(core); var focus = authorizedBodies.FirstOrDefault(b => b.Id == ObservedBodyId);
        if (ObservedBodyId != null && focus.Id == null) ObservedBodyId = null;
        var actor = core.Character; standingElevation = core.Location?.StandingElevationM ?? .1875;
        var admission=core.Connection?.Db.OwnWorldAdmission.Iter().FirstOrDefault(row=>row.CharacterId==actor?.Id);
        var ownedContext=!preview&&core.Eva==null&&vessel is {Owned:true}&&actor?.Connected==true&&core.SharedAdmissionReady&&
            core.Location?.InstanceId==core.Instance?.Id&&core.Instance!=null&&admission?.ShipId==vessel.Id;
        ownedDisplay=ownedContext?ownedMotion.Step(new(core.Connection!.Identity?.ToString()??"",actor!.Id,vessel!.Id,core.Instance!.Id,core.Location!.VisitId,
            admission!.SystemId+"/"+admission.ShipId),new(vessel.X,vessel.Y,vessel.Heading,core.Seat?.LocalX??actor.LocalX,core.Seat?.LocalY??actor.LocalY,
            interior,preferences.ReducedMotion,core.Seat?.StandingElevationM??standingElevation,core.Resting||core.IsPiloting,crew.OwnSeatLift(core),SupportElevationM:core.Seat?.StandingElevationM??standingElevation),delta):ownedMotion.Step(null,null,delta);
        if(ownedContext&&ownedDisplay==null)
        {
            presentationDeclined=true;GameplayReady=false;ActorInputPosition=Vector2.Zero;characterMarker.Visible=false;shipRoot.Visible=false;
            crew.Clear();space.Clear();remote.Clear();ground.Clear();ownExhaust.Clear();
            Status=ownedMotion.UnsupportedReason??"Owned presentation is unavailable.";return;
        }
        if(ownedDisplay is {} fresh&&fresh.ContextGeneration!=ownedGeneration)
        {
            ownedGeneration=fresh.ContextGeneration;framed=false;cameraChanged=true;orbit=.45;
            ObservedBodyId=null;observeAlpha=.45;observeBeta=SpaceMath.DeckBeta;observeRatio=shownObserveRatio=5;
            deckZoom=shownDeckZoom=initialDeckZoom;flightZoom=shownFlightZoom=initialFlightZoom;
        }
        var presentedX=ownedDisplay?.X??vessel?.X??0;var presentedY=ownedDisplay?.Y??vessel?.Y??0;
        RenderOriginX = ObservedBodyId == null ? core.Eva?.X??presentedX : focus.X; RenderOriginY = ObservedBodyId == null ? core.Eva?.Y??presentedY : focus.Y;
        shipRoot.Position = new Vector3((float)(presentedX-RenderOriginX),0,-(float)(presentedY-RenderOriginY));
        var heading = preview ? 2.9 : ownedDisplay?.Heading??vessel?.Heading??0; shipRoot.Rotation = new Vector3(0,(float)heading,0);
        ActorInputPosition=actor==null?Vector2.Zero:new Vector2((float)(ownedDisplay?.LocalX??actor.LocalX),(float)(ownedDisplay?.LocalY??actor.LocalY));
        if(core.Eva is { } eva){var local=shipRoot.Transform.AffineInverse()*new Vector3((float)(eva.X-RenderOriginX),(float)standingElevation,-(float)(eva.Y-RenderOriginY));ActorInputPosition=new Vector2(local.X,-local.Z);}
        characterMarker.Visible = !preview && actor != null && !core.IsPiloting && interior;
        if (actor != null) characterMarker.Position = ownedDisplay is {} markerFrame ? new Vector3((float)markerFrame.BodyLocalPosition.X,(float)markerFrame.BodyLocalPosition.Y+.035f,(float)markerFrame.BodyLocalPosition.Z) : new Vector3((float)actor.LocalX,(float)standingElevation+.035f,-(float)actor.LocalY);
        var frameCenter=assembly?.CameraFrame;
        var target = frameCenter is { } initialFrame ? shipRoot.Transform * new Vector3((float)initialFrame.CenterX,(float)(standingElevation+.8*blend),-(float)initialFrame.CenterY) : Vector3.Zero;
        if(core.Eva is { } body)target=new Vector3((float)(body.X-RenderOriginX),(float)standingElevation,-(float)(body.Y-RenderOriginY));
        var frameDelta=ownedDisplay?.DeltaSeconds??Math.Clamp(delta,0,.1);
        var instant = !framed || cameraChanged || preferences.ReducedMotion; var factor = instant ? 1 : 1-Math.Exp(-frameDelta*6); cameraChanged = false;
        blend = interior ? SpaceMath.Ease(blend,1,frameDelta,6) : SpaceMath.Ease(blend,0,frameDelta,6); if (instant) blend = interior ? 1 : 0;
        shownDeckZoom = preferences.ReducedMotion ? deckZoom : SpaceMath.Ease(shownDeckZoom,deckZoom,frameDelta); shownFlightZoom = preferences.ReducedMotion ? flightZoom : SpaceMath.Ease(shownFlightZoom,flightZoom,frameDelta);
        var wantedAlpha = interior ? Math.PI-heading+orbit : Math.PI/2; alpha += SpaceMath.AngleDelta(alpha,wantedAlpha) * factor; beta = SpaceMath.Ease(beta,interior?SpaceMath.DeckBeta:core.Eva!=null?.22:.015,instant?10:frameDelta,6);
        if(core.Eva==null&&frameCenter is { } sourceFrame)
        {
            var actorBlend=actor==null?0:blend*SpaceMath.DeckActorWeight(shownDeckZoom,initialDeckZoom);
            var bodyX=ownedDisplay?.BodyLocalPosition.X??actor?.LocalX??0;var bodyY=-(ownedDisplay?.BodyLocalPosition.Z??-actor?.LocalY??0);
            target=shipRoot.Transform*new Vector3((float)(sourceFrame.CenterX*(1-actorBlend)+bodyX*actorBlend),
                (float)((ownedDisplay?.BodyLocalPosition.Y??standingElevation)+.8*blend),-(float)(sourceFrame.CenterY*(1-actorBlend)+bodyY*actorBlend));
        }
        var viewport = GetViewport().GetVisibleRect(); var available = requestedPresentationBounds.Size.X > 0 ? requestedPresentationBounds.Intersection(viewport) : viewport; if (available.Size.X < 1 || available.Size.Y < 1) available = viewport; PresentationBounds = available;
        var half = shownFlightZoom*(1-blend)+shownDeckZoom*blend;
        var radius = half / Math.Tan(SpaceMath.FieldOfView/2);
        if (ObservedBodyId != null) { target = new Vector3(0,(float)focus.Height,0); alpha = observeAlpha; beta = observeBeta; shownObserveRatio = preferences.ReducedMotion ? observeRatio : SpaceMath.Ease(shownObserveRatio,observeRatio,frameDelta); var visualRadius = focus.Radius*(focus.Kind=="star"?2.1/Math.Min(1,Math.Max(.1,viewport.Size.X/viewport.Size.Y)):1); radius = Math.Max(8,visualRadius*shownObserveRatio); }
        var displacement = new Vector3((float)(Math.Cos(alpha)*Math.Sin(beta)*radius),(float)(Math.Cos(beta)*radius),(float)(Math.Sin(alpha)*Math.Sin(beta)*radius));
        Camera.HOffset = Camera.VOffset = 0; Camera.Position = target+displacement; Camera.LookAt(target,Vector3.Up); Camera.Near = (float)Math.Max(.1,radius*.02); Camera.Far = (float)Math.Max(1600,radius+1600);
        var center = available.GetCenter()-viewport.Position; var projectedHalf = radius*Math.Tan(SpaceMath.FieldOfView/2);
        if (ObservedBodyId == null) { Camera.HOffset = (float)((.5-center.X/viewport.Size.X)*projectedHalf*2*viewport.Size.X/viewport.Size.Y); Camera.VOffset = (float)((center.Y/viewport.Size.Y-.5)*projectedHalf*2); }
        if (assembly != null) { assembly.SetCutaway(interior);var localCamera=shipRoot.GlobalTransform.AffineInverse()*Camera.GlobalPosition;assembly.UpdateCutaway(localCamera);assembly.UpdateLocalLights(localCamera);ProjectedShipBounds = ProjectBounds(assembly.Bounds); }
        shipRoot.Visible = shipRoot.Position.Length() < Camera.Far*2;
        space.Sync(core,Camera,RenderOriginX,RenderOriginY,elapsed,delta,interior,preferences,target);
        remote.Sync(core,Camera,RenderOriginX,RenderOriginY,elapsed,preferences);
        if(vessel is {Owned:false} && assembly!=null)ownExhaust.SyncRemote(core,vessel.Id,assembly.PrefabId,assembly.PrefabRevision,elapsed,preferences.Glow);
        else ownExhaust.SyncOwn(core,assembly?.PrefabId,assembly?.PrefabRevision,assembly?.Theme,elapsed,preferences.Glow);
        crew.ReducedMotion=preferences.ReducedMotion;
        crew.ConfigureFrame(shipRoot,RenderOriginX,RenderOriginY,standingElevation,WorldLayer,interior,ownedDisplay); crew.Sync(core,elapsed);
        ground.Sync(core,shipRoot,interior&&!preview,WorldLayer,preferences);
        if (core.Character != null && crew.HasReady(core.Character.Id)) characterMarker.Visible = false;
        var star = space.AuthorizedBodies.FirstOrDefault(b => b.Appearance == "yellow-main-sequence-r013");
        var direction = star.Id == null ? new Vector3(-.6f,-1,.45f) : target-new Vector3((float)(star.X-RenderOriginX),(float)star.Height,-(float)(star.Y-RenderOriginY));
        lighting.Sync(direction,-Camera.GlobalBasis.Z,preferences.Lighting,preferences.Shadows,star.Id==null?Colors.White:new Color(1,.87f,.61f));
        space.BillboardAtmospheres(Camera); framed = true;
        const string unavailable=" · an exact asset pin is unavailable";
        Status=Status.Replace(unavailable,"",StringComparison.Ordinal);
        if (MissingAssetIds.Length != 0) Status += unavailable;
    }
    private Rect2 ProjectBounds(Aabb bounds)
    {
        var lo = new Vector2(float.PositiveInfinity,float.PositiveInfinity); var hi = new Vector2(float.NegativeInfinity,float.NegativeInfinity);
        for(var i=0;i<8;i++){var point=shipRoot.GlobalTransform*bounds.GetEndpoint(i);if(Camera.IsPositionBehind(point))continue;var screen=Camera.UnprojectPosition(point);lo=lo.Min(screen);hi=hi.Max(screen);}return float.IsFinite(lo.X)?new Rect2(lo,hi-lo):default;
    }
}
