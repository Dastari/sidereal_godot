using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sidereal.Native;

/// <summary>Exact browser plume geometry driven solely by accepted actuator throttle.</summary>
public partial class SpaceExhaust : Node3D
{
    public readonly record struct Nozzle(string Id, string Kind, double X, double Y, double Height, double Ex, double Ey, double Radius, double Throttle);
    private static ArrayMesh? mesh;
    private static Shader? shader;
    private static readonly Dictionary<string,ShaderMaterial> materials=new();
    private readonly Dictionary<string, (Node3D Root, MeshInstance3D[] Jets, double Phase)> jets = new();
    private uint layers = 1;
    private double phase;
    private string theme="federation";
    public int LitCount { get; private set; }
    public SpaceExhaust() { }
    public SpaceExhaust(uint layer) { layers = layer; }
    public static JsonElement FindLayout(string? prefabId, ulong? revision = null) => SpaceEnvironment.Catalog.GetProperty("nozzles").EnumerateArray()
        .FirstOrDefault(d => d.GetProperty("prefabId").GetString() == prefabId && (revision == null || d.GetProperty("revision").GetUInt64() == revision));
    private static string Mount(string id) { var at = id.IndexOf(":mount-", StringComparison.Ordinal); return at >= 0 ? id[(at + 1)..] : id; }
    private static Nozzle Read(JsonElement n, double throttle) => new(n.GetProperty("id").GetString()!, n.GetProperty("kind").GetString()!, n.GetProperty("nozzleX").GetDouble(), n.GetProperty("nozzleY").GetDouble(), n.GetProperty("height").GetDouble(), n.GetProperty("exhaustX").GetDouble(), n.GetProperty("exhaustY").GetDouble(), n.GetProperty("radius").GetDouble(), throttle);
    public void SyncOwn(ClientCore core, string? prefabId,ulong? revision,string? shipTheme, double elapsed, bool glow)
    {
        theme=shipTheme??"federation";var layout = FindLayout(prefabId,revision); var values = new List<Nozzle>();
        if (core.Connection != null && layout.ValueKind != JsonValueKind.Undefined)
        foreach (var row in core.Connection.Db.OwnAuthoredFlightActuators.Iter().Where(a => a.ShipId == core.ActiveShipId))
        {
            var id = Mount(row.PlacedObjectId); var n = layout.GetProperty("nozzles").EnumerateArray().FirstOrDefault(n => n.GetProperty("id").GetString() == id);
            if (n.ValueKind == JsonValueKind.Undefined || !double.IsFinite(row.NozzleX + row.NozzleY + row.Height + row.ExhaustX + row.ExhaustY + row.Throttle)) continue;
            var source = Read(n, row.Throttle);
            values.Add(source with { X = row.NozzleX, Y = row.NozzleY, Height = row.Height, Ex = row.ExhaustX, Ey = row.ExhaustY });
        }
        Sync(values, elapsed, glow);
    }
    public void SyncRemote(ClientCore core, string shipId, string? prefabId, ulong revision, double elapsed, bool glow)
    {
        var layout = FindLayout(prefabId, revision); var values = new List<Nozzle>();
        if(prefabId!=null)try{theme=ReplicatedWorld.Catalog.Exterior(prefabId,revision).GetProperty("theme").GetString()!;}catch(InvalidOperationException){}
        if (core.Connection != null && core.SpatialReady && layout.ValueKind != JsonValueKind.Undefined)
        foreach (var row in core.Connection.Db.VisibleActuatorExhaust.Iter().Where(a => a.ShipId == shipId))
        {
            var id = Mount(row.SourceId); var n = layout.GetProperty("nozzles").EnumerateArray().FirstOrDefault(n => n.GetProperty("id").GetString() == id);
            if (n.ValueKind != JsonValueKind.Undefined && double.IsFinite(row.Throttle)) values.Add(Read(n, row.Throttle));
        }
        Sync(values, elapsed, glow);
    }
    private static void Geometry()
    {
        if (mesh != null) return;
        var p = SpaceEnvironment.Catalog.GetProperty("plume");
        var position = p.GetProperty("positions").EnumerateArray().Select(v => v.GetSingle()).ToArray(); var normal = p.GetProperty("normals").EnumerateArray().Select(v => v.GetSingle()).ToArray(); var color = p.GetProperty("colors").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        Vector3[] Pack(float[] a) => Enumerable.Range(0,a.Length/3).Select(i => new Vector3(a[i*3],a[i*3+1],a[i*3+2])).ToArray();
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = Pack(position); arrays[(int)Mesh.ArrayType.Normal] = Pack(normal); arrays[(int)Mesh.ArrayType.Index] = p.GetProperty("indices").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        arrays[(int)Mesh.ArrayType.Color] = Enumerable.Range(0,color.Length/4).Select(i => new Color(color[i*4],color[i*4+1],color[i*4+2],color[i*4+3])).ToArray();
        mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays);
        shader = new Shader { Code = "shader_type spatial; render_mode unshaded,cull_disabled,blend_add,depth_draw_never;uniform vec3 tint;void fragment(){ALBEDO=COLOR.rgb*tint;ALPHA=COLOR.a;}" };
    }
    private void Sync(IEnumerable<Nozzle> accepted, double elapsed, bool glow)
    {
        Geometry(); LitCount = 0; var seen = new HashSet<string>();
        foreach (var n in accepted)
        {
            if (n.Throttle <= .01 || !double.IsFinite(n.Radius + n.X + n.Y + n.Height + n.Ex + n.Ey) || n.Radius <= 0) continue;
            seen.Add(n.Id);
            if (!jets.TryGetValue(n.Id,out var bank))
            {
                var root = new Node3D { Name = "AcceptedActuatorPlume" }; AddChild(root);
                var materialKey=$"{theme}:{n.Kind=="rcs"}";if(!materials.TryGetValue(materialKey,out var material)){material=new ShaderMaterial{Shader=shader};var tint=new Vector3(.85f,.95f,1);if(n.Kind!="rcs"&&ReplicatedWorld.Catalog.Root.GetProperty("themes").TryGetProperty(theme,out var source)){var c=source.GetProperty("plume");tint=new Vector3(c[0].GetSingle(),c[1].GetSingle(),c[2].GetSingle());}material.SetShaderParameter("tint",tint);materials[materialKey]=material;}
                var parts = Enumerable.Range(0,n.Kind == "reverser" ? 2 : 1).Select(_ => new MeshInstance3D { Mesh = mesh, MaterialOverride = material, Layers = layers, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off }).ToArray(); foreach (var part in parts) {root.AddChild(part);part.SetMeta("exhaust_phase",phase+=1.7);}
                bank = (root,parts,phase); jets[n.Id] = bank;
            }
            var throttle = Math.Clamp(n.Throttle,0,1); var shape = SpaceMath.ExhaustShape(n.Kind,throttle);
            bank.Root.Position = new Vector3((float)n.X,(float)n.Height,-(float)n.Y); bank.Root.Rotation = new Vector3(0,(float)Math.Atan2(n.Ex,-n.Ey),0); bank.Root.Visible = true;
            for (var i=0;i<bank.Jets.Length;i++) {var flicker=1+.08*Math.Sin(elapsed*1000*.037+bank.Jets[i].GetMeta("exhaust_phase").AsDouble()); bank.Jets[i].Rotation = new Vector3(0,n.Kind=="reverser"?(i==0?-1.22f:1.22f):0,0); bank.Jets[i].Scale = new Vector3((float)(n.Radius*shape.Width),(float)(n.Radius*shape.Width),(float)(n.Radius*shape.Length*flicker)); }
            LitCount += bank.Jets.Length;
        }
        foreach (var id in jets.Keys.Where(id => !seen.Contains(id)).ToArray()) { jets[id].Root.QueueFree(); jets.Remove(id); }
    }
    public void Clear() { foreach (var bank in jets.Values) bank.Root.QueueFree(); jets.Clear(); LitCount = 0; }
}
