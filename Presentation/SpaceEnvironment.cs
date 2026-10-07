using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sidereal.Native;

/// <summary>Actor-authorized celestial/scenery presentation, using pinned authored derivatives.</summary>
public partial class SpaceEnvironment : Node3D
{
    public const string AssetRoot = "res://Assets/Environment/";
    private static JsonDocument? catalog;
    public static JsonElement Catalog => (catalog ??= JsonDocument.Parse(FileAccess.GetFileAsString(AssetRoot + "manifest.json"))).RootElement;
    public readonly record struct Body(string Id, string Kind, string Appearance, uint Seed, double X, double Y, double Height, double Radius, double Heading, ulong Tick);
    private sealed class Entry
    {
        public Node3D Root = null!;
        public string Signature = "";
        public string? PendingFile;
        public Node3D? Active;
        public int Lod = 2;
        public ulong Serial;
    }
    private readonly Dictionary<string, Entry> bodies = new();
    private readonly Dictionary<string, Node3D> asteroids = new();
    private readonly Dictionary<string,MeshInstance3D> stellarPoints=new();
    private readonly Dictionary<string,MeshInstance3D> unresolved=new();
    private readonly HashSet<string> requests = new();
    private readonly Dictionary<string, PackedScene> loaded = new();
    private readonly HashSet<string> missing = new();
    private uint layers = 1;
    private MeshInstance3D skyMesh = null!;
    private ShaderMaterial skyMaterial = null!;
    private readonly SourceDisplaySpace skyDisplay = new();
    public string SkyDisplayStatus => skyDisplay.Status;
    public SourceDisplaySpaceProfile SkyDisplayProfile => skyDisplay.Profile;
    private MeshInstance3D stars = null!;
    private MultiMeshInstance3D dust = null!;
    private ShaderMaterial dustMaterial = null!;
    private string? dustKey;
    private readonly List<(double X,double Y,double CenterX,double CenterZ)> dustAnchors = new();
    private string? context;
    private ulong serial;
    public Body[] AuthorizedBodies { get; private set; } = Array.Empty<Body>();
    public int RenderedBodyCount => bodies.Values.Count(b => b.Active != null && b.Root.Visible);
    public int RetainedBodyCount => bodies.Count;
    public int AsteroidCount => asteroids.Count;
    public string[] MissingAssetPins => missing.Concat(unresolved.Values.Select(n=>n.GetMeta("unavailable_pin").AsString())).Distinct().ToArray();
    public int UnresolvedBodyCount => unresolved.Count;
    public string ActiveVista { get; private set; } = "deep-space";
    public string[] VistaIds => Catalog.GetProperty("vistas").EnumerateArray().Select(v => v.GetProperty("id").GetString()!).ToArray();
    public SpaceEnvironment() { }
    public SpaceEnvironment(uint layer) { layers = layer; }
    public override void _Ready()
    {
        Name = "AuthorizedSpaceEnvironment";
        BuildSky(); BuildStars();
        var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData=true, Mesh = new BoxMesh { Size = Vector3.One }, InstanceCount = 576 };
        dustMaterial=new ShaderMaterial {Shader=new Shader {Code="shader_type spatial;render_mode unshaded,skip_vertex_transform,depth_draw_never;uniform vec3 dust_offset0;uniform vec3 dust_offset1;uniform vec3 dust_offset2;uniform vec4 dust_camera;uniform vec3 dust_forward;uniform vec3 dust_streak;uniform vec3 dust_color;uniform float dust_visibility;void vertex(){vec3 offset=INSTANCE_CUSTOM.y<.5?dust_offset0:INSTANCE_CUSTOM.y<1.5?dust_offset1:dust_offset2;vec3 center=MODEL_MATRIX[3].xyz+offset;float size=INSTANCE_CUSTOM.z;if(dust_camera.w>0.)size=min(size,max(0.,dot(center-dust_camera.xyz,dust_forward))*dust_camera.w);vec3 v=VERTEX*size;v.z*=1.+(dust_streak.z-1.)*INSTANCE_CUSTOM.x;v.xz=vec2(dust_streak.x*v.x+dust_streak.y*v.z,-dust_streak.y*v.x+dust_streak.x*v.z);vec4 world=MODEL_MATRIX*vec4(v,1.);world.xyz+=offset;VERTEX=(VIEW_MATRIX*world).xyz;}void fragment(){ALBEDO=dust_color;ALPHA=dust_visibility;}"}};
        dust = new MultiMeshInstance3D { Name = "AcceptedVelocityWorldDust", Multimesh = multi, Layers = layers, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = dustMaterial, ExtraCullMargin=10000000 };
        AddChild(dust);
    }
    public override void _ExitTree()
    {
        skyMaterial?.SetShaderParameter("source_display_lut", default(Variant));
        skyDisplay.Dispose();
    }
    public void Clear()
    {
        foreach (var entry in bodies.Values) entry.Root.QueueFree(); bodies.Clear();
        foreach (var mesh in asteroids.Values) mesh.QueueFree(); asteroids.Clear();
        foreach(var point in stellarPoints.Values)point.QueueFree();stellarPoints.Clear();
        foreach(var point in unresolved.Values)point.QueueFree();unresolved.Clear();
        // Threaded immutable asset requests have no cancel API. Drain their eventual
        // results without publishing stale nodes after a disclosure/context change.
        loaded.Clear(); missing.Clear(); dustKey=null;dustAnchors.Clear();AuthorizedBodies = Array.Empty<Body>();
    }
    public static Body[] ReadBodies(ClientCore core)
    {
        var connection = core.Connection; if (connection == null || core.Character == null) return Array.Empty<Body>();
        var admission = connection.Db.OwnWorldAdmission.Iter().FirstOrDefault(a => a.CharacterId == core.Character.Id);
        if (admission == null || !core.SpatialReady) return core.CurrentPresentedShip?.Owned==true ? connection.Db.OwnSpaceBodies.Iter().Where(b => b.ShipId == core.Character.ShipId)
            .Select(b => new Body(b.Id, b.Kind, b.Appearance, b.Seed, b.X, b.Y, b.Height, b.Radius, b.Heading, b.Tick)).Where(Valid).Take(64).ToArray() : Array.Empty<Body>();
        var motion = connection.Db.VisibleBodyMotion.Iter().Where(m => m.SystemId == admission.SystemId).ToDictionary(m => m.BodyId);
        var result = connection.Db.VisibleBodyDescriptions.Iter().Where(d => motion.ContainsKey(d.BodyId))
            .Select(d => { var m = motion[d.BodyId]; return new Body(d.BodyId, d.Kind, d.Appearance, d.Seed, m.X, m.Y, d.Height, d.Radius, m.Heading, m.ServerTick); }).Where(Valid).Take(64).ToList();
        foreach (var row in connection.Db.NearbyFieldAsteroids.Iter().Where(a => a.SystemId == admission.SystemId))
            if (result.All(b => b.Id != row.Id)) { var body = new Body(row.Id, "asteroid", "asteroid", row.Seed, row.X, row.Y, row.Height, row.Radius, 0, 0); if (Valid(body)) result.Add(body); }
        return result.ToArray();
    }
    private static bool Valid(Body b) => b.Id is {Length: > 0} && b.Radius > 0 && double.IsFinite(b.X + b.Y + b.Height + b.Radius + b.Heading);
    public void Sync(ClientCore core, Camera3D camera, double originX, double originY, double elapsed, double delta, bool interior, NativePreferencesSnapshot preferences,Vector3? cameraTarget=null)
    {
        var admission = core.Connection?.Db.OwnWorldAdmission.Iter().FirstOrDefault(a => a.CharacterId == core.Character?.Id);
        var nextContext = core.Connection == null ? null : $"{core.Connection.GetHashCode()}:{core.Character?.Id}:{core.SharedWorldEpoch}:{admission?.SystemId}:{admission?.ShipId}:{admission?.Revision}";
        if (context != nextContext) { context = nextContext; Clear(); }
        skyMesh.Position = camera.Position; stars.Position = camera.Position;
        UpdateSky(core, admission?.SystemId, originX, originY, preferences);
        UpdateDust(camera,cameraTarget??Vector3.Zero, originX, originY, core.CurrentPresentedShip?.Vx ?? 0, core.CurrentPresentedShip?.Vy ?? 0, preferences.ReducedMotion, interior);
        SyncBodySnapshot(ReadBodies(core),camera,originX,originY,elapsed,preferences);
    }
    /// <summary>Rendering adapter for an already-authorized snapshot. Normal gameplay
    /// feeds this solely from actor-filtered ReadBodies; it never writes world state.</summary>
    public void SyncBodySnapshot(Body[] snapshot,Camera3D camera,double originX,double originY,double elapsed,NativePreferencesSnapshot preferences)
    {
        skyDisplay.Apply(skyMaterial,camera,GetViewport());
        AuthorizedBodies=snapshot.Where(Valid).DistinctBy(b=>b.Id).Take(8256).ToArray();
        PollRequests();
        var height = GetViewport().GetVisibleRect().Size.Y;
        UpdateDistantStars(camera,originX,originY,height);
        var selected = AuthorizedBodies.Where(b => b.Kind is "planet" or "star").Select(b => {
            var p = new Vector3((float)(b.X - originX), (float)b.Height, -(float)(b.Y - originY));
            return (Body: b, Position: p, Pixels: SpaceMath.ProjectedRadius(b.Radius, camera.Position.DistanceTo(p), height));
        }).Where(v => v.Pixels >= .25 && (v.Position-camera.Position).Dot(-camera.Basis.Z) <= camera.Far + v.Body.Radius * 8)
            .OrderByDescending(v => v.Pixels).ThenBy(v => v.Body.Id, StringComparer.Ordinal).Take(3).ToArray();
        var ids = selected.Select(v => v.Body.Id).ToHashSet();
        foreach (var pair in bodies) pair.Value.Root.Visible = ids.Contains(pair.Key);
        foreach (var item in selected)
        {
            var b = item.Body;
            JsonElement descriptor = default;
            var star = b.Kind == "star" && b.Appearance == Catalog.GetProperty("star").GetProperty("id").GetString() && b.Seed == Catalog.GetProperty("star").GetProperty("seed").GetUInt32();
            if (!star && b.Kind=="planet") descriptor = Catalog.GetProperty("bodies").EnumerateArray().FirstOrDefault(d => d.GetProperty("id").GetString() == b.Appearance && d.GetProperty("seed").GetUInt32() == b.Seed);
            if (!star && descriptor.ValueKind == JsonValueKind.Undefined) {
                // A body UUID does not authorize retaining geometry from an old pin.
                // Retire it before publishing the unresolved representation, including
                // any pending load that could otherwise attach after the pin changed.
                if(bodies.Remove(b.Id,out var stale)){stale.Root.Visible=false;stale.Root.QueueFree();TrimLoaded();}
                Unresolved(b,item.Position,camera,item.Pixels);continue;
            }
            if(unresolved.Remove(b.Id,out var missingMarker)){missingMarker.Visible=false;missingMarker.QueueFree();}
            var signature = $"{b.Appearance}:{b.Seed}:{b.Radius}";
            if (!bodies.TryGetValue(b.Id, out var entry))
            {
                if (bodies.Count >= 3) { var old = bodies.Where(p => !ids.Contains(p.Key)).OrderBy(p => p.Value.Serial).FirstOrDefault(); if (old.Value == null) continue; old.Value.Root.QueueFree(); bodies.Remove(old.Key); TrimLoaded(); }
                entry = new Entry { Root = new Node3D { Name = "ReplicatedCelestial" }, Signature = signature }; AddChild(entry.Root); bodies[b.Id] = entry;
                entry.Root.SetMeta("body_id", b.Id); entry.Root.SetMeta("appearance", b.Appearance); entry.Root.SetMeta("seed", b.Seed);
            }
            if (entry.Signature != signature) { if(entry.Active!=null){entry.Active.Visible=false;entry.Active.QueueFree();} entry.Active = null; entry.PendingFile = null; entry.Signature = signature; }
            entry.Serial = ++serial; entry.Root.Visible = true; entry.Root.Position = item.Position; entry.Root.Scale = Vector3.One * (float)b.Radius;
            var level = star ? default : descriptor.GetProperty("levels").EnumerateArray().First(v => v.GetProperty("lod").GetInt32() == (descriptor.GetProperty("fixedDetail").GetBoolean() ? 0 : SpaceMath.PlanetLod(item.Pixels, entry.Lod)));
            var lod = star ? 0 : level.GetProperty("lod").GetInt32();
            var file = star ? "star.glb" : level.GetProperty("surface").GetProperty("file").GetString()!;
            if (entry.Active == null || entry.Lod != lod)
            {
                entry.PendingFile = file;
                var surface = Load(file); var weather = star ? null : OptionalLoad(level, "weather"); var smoke = star ? null : OptionalLoad(level, "smoke");
                var weatherRequired = !star && level.GetProperty("weather").ValueKind != JsonValueKind.Null;
                var smokeRequired = !star && level.GetProperty("smoke").ValueKind != JsonValueKind.Null;
                if (surface != null && (!weatherRequired || weather != null) && (!smokeRequired || smoke != null))
                {
                    var next = new Node3D { Name = "ReadyReviewedLod" }; Configure(surface.Instantiate<Node3D>(), next, preferences,SourceLightClass.ReferenceSurface);
                    // The pinned toxic density texture uses referenceMaterial (direct1).
                    // The other reviewed cloud sheets use the explicit weather direct2.4.
                    var weatherClass=!star&&descriptor.GetProperty("weatherRole").ValueKind==JsonValueKind.Object&&
                        descriptor.GetProperty("weatherRole").TryGetProperty("alphaMode",out var alphaMode)&&alphaMode.GetString()=="BLEND"
                            ? SourceLightClass.ReferenceSurface:SourceLightClass.Weather;
                    if (weather != null) Configure(weather.Instantiate<Node3D>(), next, preferences,weatherClass);
                    if (smoke != null) Configure(smoke.Instantiate<Node3D>(), next, preferences,SourceLightClass.ReferenceSurface);
                    if (!star) AddAtmosphere(next, descriptor, preferences.Glow);
                    else SpaceStarEffects.Attach(next,layers,preferences.Glow);
                    entry.Root.AddChild(next); entry.Active?.QueueFree(); entry.Active = next; entry.Lod = lod; entry.PendingFile = null;
                }
            }
            // Exact reviewed browser compositions retain fixed local transforms; no invented axial spin.
            if(star&&entry.Active!=null)SpaceStarEffects.Update(entry.Active,elapsed);
        }
        foreach (var id in bodies.Keys.Where(id => !AuthorizedBodies.Any(b => b.Id == id)).ToArray()) { bodies[id].Root.QueueFree(); bodies.Remove(id); }
        foreach(var id in unresolved.Keys.Where(id=>!ids.Contains(id)).ToArray()){unresolved[id].QueueFree();unresolved.Remove(id);}
        UpdateAsteroids(originX, originY, preferences);
    }
    /// <summary>Explicit native smoke fixture for synthetic, already-authorized tuples.
    /// It operates only on this local renderer and cannot connect or publish state.</summary>
    public string RenderFixtureSnapshot(string snapshotJson,Camera3D camera)
    {
        if(!OS.GetCmdlineUserArgs().Contains("--space-appearance-fixture",StringComparer.Ordinal))throw new InvalidOperationException("The appearance fixture must be explicitly enabled.");
        if(snapshotJson.Length>65536)throw new ArgumentException("The appearance fixture snapshot is too large.");
        var snapshot=JsonSerializer.Deserialize<Body[]>(snapshotJson,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??Array.Empty<Body>();
        if(snapshot.Length>64)throw new ArgumentException("The appearance fixture contains too many bodies.");
        skyMesh.Position=camera.Position;stars.Position=camera.Position;dust.Visible=false;
        SyncBodySnapshot(snapshot,camera,0,0,Time.GetTicksMsec()/1000d,NativePreferences.Current.Snapshot);
        BillboardAtmospheres(camera);
        return JsonSerializer.Serialize(new{role="synthetic-authorized-appearance-fixture",rendered=RenderedBodyCount,retained=RetainedBodyCount,unresolved=UnresolvedBodyCount,pending=requests.Count,missing=MissingAssetPins});
    }
    private void Unresolved(Body body,Vector3 position,Camera3D camera,double pixels)
    {
        if(!unresolved.TryGetValue(body.Id,out var marker)){marker=new MeshInstance3D{Name="UnresolvedAuthorizedBody",Mesh=new QuadMesh{Size=Vector2.One},Layers=layers,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off,MaterialOverride=new ShaderMaterial{Shader=new Shader{Code="shader_type spatial;render_mode unshaded,cull_disabled,depth_draw_never;void fragment(){vec2 p=UV*2.-1.;float r=length(p);float ring=1.-smoothstep(.035,.08,abs(r-.7));if(ring<.1)discard;ALBEDO=vec3(.35,.7,.86);ALPHA=ring;}"}}};marker.SetMeta("body_id",body.Id);AddChild(marker);unresolved[body.Id]=marker;}
        marker.SetMeta("unavailable_appearance",body.Appearance);marker.SetMeta("unavailable_pin",$"{body.Appearance}:seed{body.Seed}");
        marker.Position=position;var scale=(float)(Math.Max(8,Math.Min(pixels*2,32))*2*camera.Position.DistanceTo(position)*Math.Tan(SpaceMath.FieldOfView/2)/Math.Max(1,GetViewport().GetVisibleRect().Size.Y));marker.Basis=camera.Basis.Orthonormalized().Scaled(Vector3.One*scale);
    }
    private void PollRequests()
    {
        foreach(var file in requests.ToArray()) {var status=ResourceLoader.LoadThreadedGetStatus(AssetRoot+file);if(status is ResourceLoader.ThreadLoadStatus.InProgress)continue;
            if(status==ResourceLoader.ThreadLoadStatus.Loaded){var result=ResourceLoader.LoadThreadedGet(AssetRoot+file) as PackedScene;var needed=bodies.Values.Any(e=>e.PendingFile==file||e.PendingFile!=null&&file.StartsWith(e.PendingFile[..^4]+"-",StringComparison.Ordinal))||file=="asteroid.glb"&&AuthorizedBodies.Any(b=>b.Kind=="asteroid");if(needed&&result!=null)loaded[file]=result;}
            else missing.Add(file);requests.Remove(file);
        }
    }
    private void UpdateDistantStars(Camera3D camera,double x,double y,float height)
    {
        var accepted=AuthorizedBodies.Where(b=>b.Kind=="star").ToArray();foreach(var id in stellarPoints.Keys.Where(id=>!accepted.Any(b=>b.Id==id)).ToArray()){stellarPoints[id].QueueFree();stellarPoints.Remove(id);}
        foreach(var b in accepted){if(!stellarPoints.TryGetValue(b.Id,out var point)){point=new MeshInstance3D{Name="AcceptedDistantStellarLight",Mesh=new QuadMesh{Size=Vector2.One},Layers=layers,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off,MaterialOverride=new ShaderMaterial{Shader=new Shader{Code="shader_type spatial;render_mode unshaded,cull_disabled,blend_add,depth_draw_never;void fragment(){float r=length((UV-.5)*2.);float a=exp(-r*r*12.)*(1.-smoothstep(.7,1.,r));ALBEDO=vec3(1.,.72,.26)*a;ALPHA=a;}"}}};point.SetMeta("body_id",b.Id);AddChild(point);stellarPoints[b.Id]=point;}
            var position=new Vector3((float)(b.X-x),(float)b.Height,-(float)(b.Y-y));var direction=position-camera.Position;var distance=direction.Length();var projected=SpaceMath.ProjectedRadius(b.Radius,distance,height);point.Visible=projected<2&&distance>0;if(!point.Visible)continue;var depth=Math.Min(distance,camera.Far*.85f);point.Position=camera.Position+direction*(depth/distance);point.Basis=camera.Basis.Orthonormalized().Scaled(Vector3.One*(float)(2*depth*Math.Tan(SpaceMath.FieldOfView/2)/Math.Max(1,height)*8));
        }
    }
    public void BillboardAtmospheres(Camera3D camera)
    {
        foreach(var e in bodies.Values)if(e.Active!=null)foreach(var halo in e.Active.GetChildren().OfType<MeshInstance3D>().Where(n=>n.HasMeta("celestial_halo")))
            halo.Basis=halo.GetParent<Node3D>().GlobalBasis.Orthonormalized().Inverse()*camera.GlobalBasis.Orthonormalized();
    }
    public void ApplyPreferences(NativePreferencesSnapshot p)
    {
        foreach(var e in bodies.Values)Visit(e.Root);
        foreach(var a in asteroids.Values)Visit(a);
        void Visit(Node n){if(n is GeometryInstance3D g)g.CastShadow=p.Shadows&&!g.HasMeta("celestial_halo")?GeometryInstance3D.ShadowCastingSetting.On:GeometryInstance3D.ShadowCastingSetting.Off;if(n.HasMeta("celestial_halo")&&n is Node3D halo)halo.Visible=p.Glow;foreach(var c in n.GetChildren())Visit(c);}
    }
    private void TrimLoaded()
    {
        var used = bodies.Values.SelectMany(e => e.Active?.GetChildren().OfType<Node3D>() ?? Enumerable.Empty<Node3D>()).Select(n => n.GetMeta("asset_path", "").AsString()).ToHashSet();
        foreach (var file in loaded.Keys.Where(f => !used.Contains(f)).ToArray()) loaded.Remove(file);
    }
    private PackedScene? OptionalLoad(JsonElement level, string kind) => level.GetProperty(kind).ValueKind == JsonValueKind.Null ? null : Load(level.GetProperty(kind).GetProperty("file").GetString()!);
    private PackedScene? Load(string file)
    {
        if (loaded.TryGetValue(file, out var scene)) return scene;
        if(missing.Contains(file))return null;
        var path = AssetRoot + file;
        if (!requests.Contains(file)) { if (!ResourceLoader.Exists(path)) { missing.Add(file); return null; } if (ResourceLoader.LoadThreadedRequest(path, "PackedScene") != Error.Ok) { missing.Add(file); return null; } requests.Add(file); }
        var status = ResourceLoader.LoadThreadedGetStatus(path);
        if (status == ResourceLoader.ThreadLoadStatus.Loaded) { scene = ResourceLoader.LoadThreadedGet(path) as PackedScene;requests.Remove(file); if (scene == null) { missing.Add(file); return null; } loaded[file] = scene; return scene; }
        if (status is ResourceLoader.ThreadLoadStatus.Failed or ResourceLoader.ThreadLoadStatus.InvalidResource) missing.Add(file);
        return null;
    }
    private void Configure(Node3D node, Node3D parent, NativePreferencesSnapshot p,SourceLightClass source)
    {
        node.SetMeta("asset_path", node.SceneFilePath.Replace(AssetRoot, "")); parent.AddChild(node);
        void Visit(Node n) { if (n is MeshInstance3D mesh) { SourceLightUnits.SetReceiver(mesh,source,layers,false); mesh.CastShadow = p.Shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off; } foreach (var child in n.GetChildren()) Visit(child); } Visit(node);
    }
    private void AddAtmosphere(Node3D parent, JsonElement descriptor, bool enabled)
    {
        var style = descriptor.GetProperty("style").GetString(); var color = style switch { "desert" => "f5aa62", "rock" or "moon" => "a5a2c6", "ice" => "64d6ff", "volcanic" => "ff7429", "toxic" => "b8e943", "gas" => "a677ff", "crystal" => "da65ff", _ => "86cfff" };
        var shader = new Shader { Code = "shader_type spatial; render_mode unshaded,cull_disabled,depth_draw_never,blend_add; uniform vec3 tint:source_color; uniform float strength; void fragment(){ float r=length((UV-.5)*2.)*1.27;float falloff=exp(-pow(max(0.,r-.955)*8.,1.4));float edge=1.-smoothstep(1.08,1.27,r);ALBEDO=mix(tint,vec3(.8,.94,1.),.2);ALPHA=falloff*edge*strength*1.6; }" };
        var material = new ShaderMaterial { Shader = shader }; material.SetShaderParameter("tint", new Color(color)); material.SetShaderParameter("strength", descriptor.GetProperty("effects").GetProperty("atmosphere").GetSingle());
        var halo = new MeshInstance3D { Name = "AuthoredAtmosphere", Mesh = new QuadMesh { Size = new Vector2(2.54f, 2.54f) }, Layers = layers, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = enabled };
        halo.SetMeta("celestial_halo", true); parent.AddChild(halo);
    }
    private void UpdateAsteroids(double x, double y, NativePreferencesSnapshot p)
    {
        var accepted = AuthorizedBodies.Where(b => b.Kind == "asteroid").Take(8192).ToArray();
        foreach (var id in asteroids.Keys.Where(id => !accepted.Any(b => b.Id == id)).ToArray()) { asteroids[id].QueueFree(); asteroids.Remove(id); }
        var source = accepted.Length == 0 ? null : Load("asteroid.glb"); if (source == null) return;
        foreach (var b in accepted)
        {
            if (!asteroids.TryGetValue(b.Id, out var node))
            {
                node = new Node3D { Name="PhysicalAsteroid" }; var original=source.Instantiate<Node3D>();node.AddChild(original);var parts=Meshes(original).Where(m=>m.Mesh!=null).ToArray();if(parts.Length==0){node.Free();continue;}
                var radius=0f;foreach(var part in parts){SourceLightUnits.SetReceiver(part,SourceLightClass.ReferenceSurface,layers,false);part.CastShadow=p.Shadows?GeometryInstance3D.ShadowCastingSetting.On:GeometryInstance3D.ShadowCastingSetting.Off;var transform=Transform3D.Identity;var chain=new Stack<Node3D>();for(var at=(Node?)part;at!=null&&at!=node;at=at.GetParent())if(at is Node3D spatial)chain.Push(spatial);foreach(var spatial in chain)transform*=spatial.Transform;var box=part.Mesh!.GetAabb();for(var i=0;i<8;i++)radius=Math.Max(radius,(transform*box.GetEndpoint(i)).Length());}
                node.SetMeta("source_radius",radius);asteroids[b.Id]=node;node.SetMeta("body_id",b.Id);AddChild(node);
            }
            node.Scale=Vector3.One*(float)(b.Radius/Math.Max(.001,node.GetMeta("source_radius").AsDouble()));node.Position=new Vector3((float)(b.X-x),(float)b.Height,-(float)(b.Y-y));node.Rotation=new Vector3(0,(float)b.Heading,0);
        }
    }
    private static IEnumerable<MeshInstance3D> Meshes(Node n) { if (n is MeshInstance3D m) yield return m; foreach (var c in n.GetChildren()) foreach (var m2 in Meshes(c)) yield return m2; }
    private void UpdateSky(ClientCore core, string? system, double x, double y, NativePreferencesSnapshot p)
    {
        var scape = core.Connection?.Db.AdmittedSystemScapes.Iter().FirstOrDefault(s => s.Id == system);
        var weights = scape == null ? new Dictionary<string, double> { [p.VistaId] = 1 } : SpaceMath.BackgroundWeights(scape.RegionsJson, x, y);
        ActiveVista = weights.OrderByDescending(v => v.Value).FirstOrDefault().Key ?? "deep-space";
        var tint = Vector3.Zero; var tint2 = Vector3.Zero; var gas = 0d; var dustStrength = 0d;
        foreach (var w in weights) {
            var vista = Catalog.GetProperty("vistas").EnumerateArray().FirstOrDefault(v => v.GetProperty("id").GetString() == w.Key); if (vista.ValueKind == JsonValueKind.Undefined) continue;
            var t = vista.GetProperty("nebulaTint"); var color = new Vector3(t[0].GetSingle(), t[1].GetSingle(), t[2].GetSingle()) * (float)(w.Value * vista.GetProperty("nebulaStrength").GetDouble());
            if (vista.TryGetProperty("nebulaAsset", out var asset) && asset.ValueKind==JsonValueKind.String) tint2 += color; else tint += color;
            if(w.Key!="deep-space")gas+=w.Value;dustStrength+=w.Value*vista.GetProperty("dust").GetDouble();
        }
        skyMaterial.SetShaderParameter("tint", tint); skyMaterial.SetShaderParameter("tint2", tint2); skyMaterial.SetShaderParameter("gas_strength", (float)gas); skyMaterial.SetShaderParameter("seed", 17f);
        dust.Visible = dustStrength > 0;dustMaterial.SetShaderParameter("dust_visibility",(float)Math.Clamp(dustStrength,0,1));
    }
    private void UpdateDust(Camera3D camera,Vector3 target, double x, double y, double vx, double vy, bool reduced, bool interior)
    {
        var viewport=GetViewport().GetVisibleRect().Size;var aspect=viewport.X/Math.Max(1,viewport.Y);var half=camera.Position.DistanceTo(target)*Math.Tan(SpaceMath.FieldOfView/2);var motion=SpaceMath.DustMotion(vx,vy,reduced);
        var depth=interior?Array.Empty<SpaceMath.DustDepth>():SpaceMath.DustDepthLayers(new(camera.Position.X,camera.Position.Y,camera.Position.Z),new(target.X,target.Y,target.Z),SpaceMath.FieldOfView,aspect);
        var strata=depth.Length!=0?depth:new[]{new SpaceMath.DustDepth(double.NaN,target.X,target.Z,0,half*aspect,half,0,1)};
        var layouts=strata.Select(s=>SpaceMath.DustLayout(s.HalfZ,s.HalfX/Math.Max(1,s.HalfZ),576/strata.Length)).ToArray();
        var next=string.Join('|',strata.Select((s,i)=>$"{Math.Floor((x+s.CenterX)/layouts[i].Spacing)},{Math.Floor((y-s.CenterZ)/layouts[i].Spacing)},{layouts[i].Spacing},{layouts[i].Columns},{layouts[i].Rows},{s.Height},{s.Thickness},{s.SizeScale}"));
        if(next!=dustKey){dustKey=next;dustAnchors.Clear();var count=0;for(var layer=0;layer<strata.Length;layer++){var s=strata[layer];var grid=layouts[layer];var ax=x+s.CenterX;var ay=y-s.CenterZ;dustAnchors.Add((ax,ay,s.CenterX,s.CenterZ));for(var i=0;i<grid.Count;i++){var cell=SpaceMath.DustCell(i,ax,ay,grid.Spacing,grid.Columns,grid.Rows);var height=double.IsNaN(s.Height)?cell.Height:s.Height+(cell.Height+33)/42*s.Thickness;dust.Multimesh.SetInstanceTransform(count,new Transform3D(Basis.Identity,new Vector3((float)(cell.X+s.CenterX),(float)height,(float)(-cell.Y+s.CenterZ))));dust.Multimesh.SetInstanceCustomData(count,new Color((float)cell.LengthVariation,layer,(float)(cell.Size*s.SizeScale),0));count++;}}dust.Multimesh.VisibleInstanceCount=count;}
        for(var i=0;i<strata.Length;i++){var s=strata[i];var a=dustAnchors[i];dustMaterial.SetShaderParameter("dust_offset"+i,new Vector3((float)(a.X-(x+s.CenterX)+s.CenterX-a.CenterX),0,(float)(-a.Y+(y-s.CenterZ)+s.CenterZ-a.CenterZ)));}
        var forward=(target-camera.Position).Normalized();dustMaterial.SetShaderParameter("dust_camera",new Vector4(camera.Position.X,camera.Position.Y,camera.Position.Z,depth.Length!=0?(float)(6*Math.Tan(SpaceMath.FieldOfView/2)/Math.Max(1,viewport.Y)):0));dustMaterial.SetShaderParameter("dust_forward",forward);dustMaterial.SetShaderParameter("dust_streak",new Vector3((float)Math.Cos(motion.Heading),(float)Math.Sin(motion.Heading),(float)motion.StreakRatio));dustMaterial.SetShaderParameter("dust_color",new Vector3((float)(.36*motion.Intensity),(float)(.57*motion.Intensity),(float)(.82*motion.Intensity)));if(depth.Length!=0)camera.Far=Math.Max(camera.Far,(float)(depth[2].DepthDistance*1.5));
    }
    // Use the renderer-specific reverse-Z far plane for both sky and stars.
    private void BuildStars()
    {
        var vertices = new List<Vector3>(); var uvs = new List<Vector2>(); var colors = new List<Color>(); var indices = new List<int>();
        foreach(var star in SpaceMath.Stars()) {
            var n = new Vector3((float)star.X,(float)star.Y,(float)star.Z); var axis = Math.Abs(n.Y) > .98 ? Vector3.Right : Vector3.Up; var u = axis.Cross(n).Normalized(); var v = n.Cross(u).Normalized(); var start = vertices.Count;
            foreach(var corner in new[] {new Vector2(-1,-1),new Vector2(1,-1),new Vector2(1,1),new Vector2(-1,1)}) { vertices.Add((n + u * (float)(corner.X * star.AngularRadius) + v * (float)(corner.Y * star.AngularRadius)) * 600); uvs.Add((corner + Vector2.One) / 2); colors.Add(new Color((float)star.Red,(float)star.Green,(float)star.Blue,1)); }
            indices.AddRange(new[] {start,start+1,start+2,start,start+2,start+3});
        }
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max); arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray(); arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray(); arrays[(int)Mesh.ArrayType.Color] = colors.ToArray(); arrays[(int)Mesh.ArrayType.Index] = indices.ToArray(); var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays);
        var material = new ShaderMaterial { Shader = new Shader { Code = "shader_type spatial;render_mode unshaded,cull_disabled,depth_draw_never,blend_add; void vertex(){POSITION=PROJECTION_MATRIX*MODELVIEW_MATRIX*vec4(VERTEX,1.);\nPOSITION.z=CLIP_SPACE_FAR*POSITION.w;\n} void fragment(){vec2 q=UV*2.-1.;float r=length(q);float aa=max(fwidth(r),.035);ALBEDO=COLOR.rgb;ALPHA=(1.-smoothstep(1.-aa,1.+aa,r))*exp(-r*r*1.8);}" } };
        stars = new MeshInstance3D { Name = "Exact8192AngularStars", Mesh = mesh, Layers = layers, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 10000000 }; AddChild(stars);
    }
    private void BuildSky()
    {
        // Order the backdrop before additive stars, dust and celestial halos. Giving
        // it an explicit alpha also keeps it out of the opaque depth prepass.
        skyMaterial = new ShaderMaterial { RenderPriority = -128, Shader = new Shader { Code = SkyShader.Replace("// SOURCE_DISPLAY_ADAPTER",SourceDisplaySpace.Shader,StringComparison.Ordinal) } }; skyMaterial.SetShaderParameter("nebula",GD.Load<Texture2D>(AssetRoot + "veil-nebula-v1.png")); skyMaterial.SetShaderParameter("nebula2",GD.Load<Texture2D>(AssetRoot + "orion-veil-v1.png"));
        skyMesh = new MeshInstance3D { Name = "PinnedWorldDirectionalGalaxy", Mesh = new SphereMesh { Radius = 600,Height = 1200,RadialSegments = 48,Rings = 24 }, MaterialOverride = skyMaterial, Layers = layers, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 10000000 }; AddChild(skyMesh);
    }
    private const string SkyShader = """
shader_type spatial;
render_mode unshaded,cull_front,depth_draw_never;
// The source samples encoded PNG values directly in its WebGL fragment path.
uniform sampler2D nebula:filter_nearest,repeat_disable;
uniform sampler2D nebula2:filter_nearest,repeat_disable;
// SOURCE_DISPLAY_ADAPTER
uniform vec3 tint; uniform vec3 tint2; uniform float gas_strength; uniform float seed;
varying vec3 direction;
float hash31(vec3 p){p=fract(p*.1031);p+=dot(p,p.yzx+33.33);return fract((p.x+p.y)*p.z);}
float noise3(vec3 p){vec3 i=floor(p),f=fract(p);f=f*f*(3.-2.*f);return mix(mix(mix(hash31(i),hash31(i+vec3(1,0,0)),f.x),mix(hash31(i+vec3(0,1,0)),hash31(i+vec3(1,1,0)),f.x),f.y),mix(mix(hash31(i+vec3(0,0,1)),hash31(i+vec3(1,0,1)),f.x),mix(hash31(i+vec3(0,1,1)),hash31(i+vec3(1,1,1)),f.x),f.y),f.z);}
float fbm(vec3 p){float f=0.,a=.5;for(int i=0;i<5;i++){f+=a*noise3(p);p=p*2.03+vec3(7.2,3.7,1.4);a*=.5;}return f;}
vec3 sky_plate(vec3 p,vec3 core){vec3 right=normalize(cross(core,vec3(0.,1.,0.)));vec3 up=cross(right,core);float forward=dot(p,core);vec2 uv=vec2(dot(p,right),dot(p,up))/max(.1,forward)*1.3+.5;uv=(floor(uv*vec2(1536.,1024.))+.5)/vec2(1536.,1024.);float seam=smoothstep(0.,.15,uv.x)*smoothstep(0.,.15,1.-uv.x)*smoothstep(.1,.5,forward);float poles=smoothstep(0.,.15,uv.y)*smoothstep(0.,.15,1.-uv.y);return vec3(clamp(uv,vec2(.001),vec2(.999)),seam*poles);}
void vertex(){direction=VERTEX;POSITION=PROJECTION_MATRIX*MODELVIEW_MATRIX*vec4(VERTEX,1.);
POSITION.z=CLIP_SPACE_FAR*POSITION.w;
}
void fragment(){vec3 p=normalize(direction);vec3 rpg=sky_plate(p,normalize(vec3(.74,-.58,.355)));vec3 down=sky_plate(p,normalize(vec3(-.20,-.96,-.18)));vec3 image=mix(texture(nebula,rpg.xy).rgb*rpg.z,texture(nebula,down.xy).rgb*down.z,smoothstep(.78,.97,-p.y));vec3 image2=mix(texture(nebula2,rpg.xy).rgb*rpg.z,texture(nebula2,down.xy).rgb*down.z,smoothstep(.78,.97,-p.y));float cloud=fbm(p*4.5+seed);float overhead=smoothstep(.25,.85,-p.y);float ribbon=max(exp(-pow(abs((p.y+.15+p.x*.45)/.36),2.))*.38,exp(-pow(abs((p.x*.55+p.z-.05)/.38),2.))*overhead);vec3 gas=mix(vec3(.03,.06,.18),vec3(.24,.035,.31),smoothstep(.35,.7,cloud));gas*=smoothstep(.2,.72,cloud)*(.4+.6*ribbon);vec3 color=vec3(.005,.008,.028)+gas*.55*gas_strength+(image*tint+image2*tint2)*.65;ALBEDO=source_display_adapter(floor(color*192.+.5)/192.);ALPHA=1.;}
""";
}
