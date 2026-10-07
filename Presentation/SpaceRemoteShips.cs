using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Sidereal.Native;

/// <summary>Every admitted shared ship is represented; public exteriors never disclose interiors.</summary>
public partial class SpaceRemoteShips : Node3D
{
    private sealed class Entry
    {
        public Node3D Root = null!;
        public MeshInstance3D Marker = null!;
        public Node3D? Full, Proxy;
        public SpaceExhaust Exhaust = null!;
        public string Asset = "";
        public ulong Revision;
        public string? PrefabId;
        public double Radius = 8;
        public int Lod = 2;
        public readonly SpaceMotionHistory Motion=new();
    }
    private readonly Dictionary<string,Entry> ships = new();
    private readonly Dictionary<string,ReplicatedWorldAssembly> prototypes = new();
    private readonly Dictionary<string,PackedScene> proxies = new();
    private readonly HashSet<string> missing = new();
    private uint layers = 1;
    private string? context;
    private NativePreferencesSnapshot? appliedPreferences;
    public int RepresentedCount => ships.Count;
    public int FullCount => ships.Values.Count(e => e.Full?.Visible == true);
    public string[] MissingAssetPins => missing.ToArray();
    public SpaceRemoteShips() { }
    public SpaceRemoteShips(uint layer) { layers=layer; }
    public void Clear() { foreach(var e in ships.Values)e.Root.QueueFree();ships.Clear();foreach(var p in prototypes.Values)p.QueueFree();prototypes.Clear();proxies.Clear();missing.Clear(); }
    public void Sync(ClientCore core,Camera3D camera,double x,double y,double elapsed,NativePreferencesSnapshot prefs)
    {
        var admission=core.Connection?.Db.OwnWorldAdmission.Iter().FirstOrDefault(a=>a.CharacterId==core.Character?.Id);
        var next=core.Connection==null?null:$"{core.Connection.GetHashCode()}:{core.Character?.Id}:{core.SharedWorldEpoch}:{admission?.SystemId}:{admission?.Revision}";
        if(next!=context){context=next;Clear();}
        if(core.Connection==null||admission==null||!core.SpatialReady){Clear();return;}
        if(appliedPreferences!=prefs){appliedPreferences=prefs;foreach(var e in ships.Values){if(e.Full!=null)Apply(e.Full,prefs);if(e.Proxy!=null)Apply(e.Proxy,prefs);}}
        var descriptions=core.Connection.Db.VisibleShipDescriptions.Iter().ToDictionary(d=>d.ShipId);
        var accepted=core.Connection.Db.VisibleShipMotion.Iter().Where(m=>m.SystemId==admission.SystemId&&m.ShipId!=core.CurrentPresentedShip?.Id&&double.IsFinite(m.X+m.Y+m.Vx+m.Vy+m.Heading+m.Omega)).ToArray();
        var seen=accepted.Select(m=>m.ShipId).ToHashSet();foreach(var id in ships.Keys.Where(id=>!seen.Contains(id)).ToArray()){ships[id].Root.QueueFree();ships.Remove(id);}
        var height=GetViewport().GetVisibleRect().Size.Y;var candidates=new List<(Entry Entry,double Pixels)>();
        foreach(var m in accepted)
        {
            descriptions.TryGetValue(m.ShipId,out var desc);
            var asset=desc?.PublishedExteriorAssetId??"";var revision=desc?.AppearanceRevision??0;
            if(ships.TryGetValue(m.ShipId,out var old)&&(old.Asset!=asset||old.Revision!=revision)){old.Root.QueueFree();ships.Remove(m.ShipId);}
            if(!ships.TryGetValue(m.ShipId,out var e))
            {
                var root=new Node3D{Name="AuthorizedRemoteShip"};AddChild(root);
                var marker=new MeshInstance3D{Name="PublishedExteriorContact",Mesh=new QuadMesh{Size=Vector2.One},Layers=layers,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off,
                    MaterialOverride=new StandardMaterial3D{AlbedoColor=new Color("68cbeb"),ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,BillboardMode=BaseMaterial3D.BillboardModeEnum.Enabled,Transparency=BaseMaterial3D.TransparencyEnum.Alpha,CullMode=BaseMaterial3D.CullModeEnum.Disabled}};root.AddChild(marker);
                var exhaust=new SpaceExhaust(layers);root.AddChild(exhaust);e=new Entry{Root=root,Marker=marker,Exhaust=exhaust,Asset=asset,Revision=revision,PrefabId=asset.StartsWith("prefab:",StringComparison.Ordinal)?asset[7..]:null};ships[m.ShipId]=e;root.SetMeta("ship_id",m.ShipId);
                if(e.PrefabId!=null)try{ReplicatedWorld.Catalog.Exterior(e.PrefabId,revision);}catch(InvalidOperationException){missing.Add($"{asset}@r{revision}");}
            }
            var now=(double)Time.GetTicksMsec();e.Motion.Remember(new(m.SystemId,m.ServerTick,m.X,m.Y,m.Vx,m.Vy,m.Heading,m.Omega),now);if(e.Motion.At(now) is not { } sampled){e.Root.Visible=false;continue;}e.Root.Visible=true;e.Root.SetMeta("motion_stale",sampled.Stale);var pose=sampled.Pose;
            e.Root.Position=new Vector3((float)(pose.X-x),0,-(float)(pose.Y-y));e.Root.Rotation=new Vector3(0,(float)pose.Heading,0);
            var distance=Math.Max(.1,camera.Position.DistanceTo(e.Root.Position));var px=SpaceMath.ProjectedRadius(e.Radius,distance,height);candidates.Add((e,px));
            e.Marker.Scale=Vector3.One*(float)(14*2*distance*Math.Tan(SpaceMath.FieldOfView/2)/Math.Max(1,height));
            e.Exhaust.SyncRemote(core,m.ShipId,e.PrefabId,revision,elapsed,prefs.Glow);
        }
        var fullBudget=0;var builtThisFrame=false;
        foreach(var candidate in candidates.OrderByDescending(c=>c.Pixels))
        {
            var e=candidate.Entry;var lod=SpaceMath.ShipLod(candidate.Pixels,e.Lod);if(lod==0&&fullBudget++>=24)lod=1;
            var pin=$"{e.PrefabId}@r{e.Revision}";
            if(lod<=1&&e.PrefabId!=null&&e.Proxy==null)
            {
                var proxy=SpaceEnvironment.Catalog.GetProperty("proxies").EnumerateArray().FirstOrDefault(p=>p.GetProperty("prefabId").GetString()==e.PrefabId&&p.GetProperty("revision").GetUInt64()==e.Revision);
                if(proxy.ValueKind!=System.Text.Json.JsonValueKind.Undefined){if(!proxies.TryGetValue(pin,out var packed)){packed=GD.Load<PackedScene>(SpaceEnvironment.AssetRoot+proxy.GetProperty("file").GetString());if(packed!=null)proxies[pin]=packed;}if(packed!=null){e.Proxy=packed.Instantiate<Node3D>();e.Root.AddChild(e.Proxy);Apply(e.Proxy,prefs);}}
            }
            if(lod==0&&e.PrefabId!=null&&e.Full==null&&!builtThisFrame)
            {
                try {if(!prototypes.TryGetValue(pin,out var prototype)){prototype=ReplicatedWorld.CreateExteriorAssembly(e.PrefabId,e.Revision,layers);prototype.Visible=false;AddChild(prototype);prototypes[pin]=prototype;builtThisFrame=true;}var lo=prototype.Bounds.Position;var hi=prototype.Bounds.End;var bx=Math.Max(Math.Abs(lo.X),Math.Abs(hi.X));var bz=Math.Max(Math.Abs(lo.Z),Math.Abs(hi.Z));e.Radius=Math.Max(2,Math.Sqrt(bx*bx+bz*bz));e.Full=prototype.Duplicate() as Node3D;if(e.Full!=null){e.Root.AddChild(e.Full);Apply(e.Full,prefs);}}
                catch(InvalidOperationException){missing.Add($"{e.Asset}@r{e.Revision}");}
            }
            if(e.Full!=null)e.Full.Visible=lod==0;if(e.Proxy!=null)e.Proxy.Visible=lod<=1&&e.Full?.Visible!=true;e.Marker.Visible=e.Full?.Visible!=true&&e.Proxy?.Visible!=true;e.Lod=lod;
            if(lod!=0&&e.Full!=null){e.Full.QueueFree();e.Full=null;}
        }
        var livePins=ships.Values.Select(e=>$"{e.PrefabId}@r{e.Revision}").ToHashSet();foreach(var pin in prototypes.Keys.Where(pin=>!livePins.Contains(pin)).ToArray()){prototypes[pin].QueueFree();prototypes.Remove(pin);proxies.Remove(pin);}
    }
    private void Apply(Node node,NativePreferencesSnapshot p){if(node is GeometryInstance3D mesh){mesh.Layers=layers;mesh.CastShadow=p.Shadows?GeometryInstance3D.ShadowCastingSetting.On:GeometryInstance3D.ShadowCastingSetting.Off;}foreach(var child in node.GetChildren())Apply(child,p);}
}
