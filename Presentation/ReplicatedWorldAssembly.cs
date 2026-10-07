using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sidereal.Native;

/// <summary>Pinned game GLBs assembled with the browser's exact public presentation plan.</summary>
public partial class ReplicatedWorldAssembly : Node3D
{
    private const string Assets = "res://Assets/World/";
    private static readonly Dictionary<string, PackedScene> scenes = new();
    private static readonly Dictionary<string, Material> materials = new();
    private static readonly Dictionary<string,(Vector3[] Vertices,int[] Indices)> pickChannels=new();
    private readonly List<(Node3D Node, string View, string OcclusionSide)> banks = new();
    private readonly List<(Aabb Bounds, string View, string OcclusionSide)> framingBounds = new();
    private readonly JsonElement ship;
    private readonly uint layers;
    private readonly IReadOnlyDictionary<string, FurnishingPose> furnishings = new Dictionary<string, FurnishingPose>();
    private readonly Dictionary<string, int> placementCounts = new();
    private readonly HashSet<string> loaded = new();
    private readonly HashSet<string> missing = new();
    private readonly bool exteriorOnly;
    private readonly List<JsonElement> lightSockets = new();
    private readonly List<OmniLight3D> lightPool = new();
    private NativePreferencesSnapshot preferences = new();
    private readonly List<(string Id, string Role, Transform3D Transform, Vector3[] Vertices, int[] Indices, Aabb Bounds, string View, string Side)> pickGeometry = new();
    public readonly record struct Hit(string PlacementId, string Role, Vector3 LocalPoint, float Distance);
    public int EffectiveLocalLights => lightPool.Count(l => l.Visible) / 2;
    public int EffectiveNativeLocalLights => lightPool.Count(l => l.Visible);
    private Aabb bounds;
    private bool hasBounds;
    private bool currentCutaway;
    private string foregroundSide = "";
    public Aabb Bounds => bounds;
    public ReplicatedWorldCatalog.CameraFrame CameraFrame { get; private set; }
    public int LoadedAssetCount => loaded.Count;
    public int RenderedPlacementCount { get; private set; }
    public IReadOnlyCollection<string> MissingAssetIds => missing;
    public string PrefabId => ship.GetProperty("id").GetString()!;
    public ulong PrefabRevision => ship.GetProperty("revision").GetUInt64();
    public string Theme => ship.GetProperty("theme").GetString()!;
    public string VisualKind => ship.GetProperty("visualKind").GetString()!;
    public string DisplayName => ship.GetProperty("name").GetString()!;

    public ReplicatedWorldAssembly() { }
    public ReplicatedWorldAssembly(JsonElement ship, string furnishingsJson, uint layers, bool cutaway, bool exteriorOnly = false)
    {
        this.ship = ship; this.layers = layers; this.exteriorOnly = exteriorOnly;
        CameraFrame=ReplicatedWorldCatalog.ReadCameraFrame(ship);
        furnishings = ReplicatedWorldCatalog.ReadFurnishings(furnishingsJson);
        ReplicatedWorldCatalog.ValidateFurnishings(ship, furnishings);
        Name = "AuthoredShipAssembly";
        Build(cutaway);
    }

    private void Build(bool cutaway)
    {
        var mounts = ship.GetProperty("prefab").GetProperty("mounts").EnumerateArray()
            .Where(m => m.GetProperty("attach").GetString() == "interior").SelectMany(m => new[] { "mount:" + m.GetProperty("id").GetString(), "mount-" + m.GetProperty("id").GetString() }).ToHashSet();
        var placements = ship.GetProperty("placements").EnumerateArray().Where(p => !exteriorOnly ||
            p.GetProperty("view").GetString() != "deck" && !mounts.Any(id => p.GetProperty("id").GetString()!.StartsWith(id,StringComparison.Ordinal))).ToArray();
        foreach (var group in placements.GroupBy(p => string.Join('|',
            p.GetProperty("file").GetString(), p.GetProperty("node").GetString(),
            p.GetProperty("role").GetString(), p.GetProperty("view").GetString(),
            p.GetProperty("authored").GetBoolean() ? "authored" : "legacy", OcclusionSide(p),
            p.TryGetProperty("themeSlots", out var themed) && themed.GetBoolean() ? "template" : "direct")))
        {
            var first = group.First();
            var file = first.GetProperty("file").GetString()!;
            try
            {
                if (!scenes.TryGetValue(file, out var scene))
                {
                    scene = GD.Load<PackedScene>(Assets + file)
                        ?? throw new InvalidOperationException("Missing authored mesh.");
                    scenes[file] = scene;
                }
                var prototype = scene.Instantiate<Node3D>();
                try
                {
                    var nodeName = first.GetProperty("node").GetString();
                    var selected = nodeName == null ? prototype : FindNode(prototype, nodeName)
                        ?? throw new InvalidOperationException("Missing selected authored mesh node.");
                    var transforms = new List<Transform3D>();
                    var entries = new List<JsonElement>();
                    foreach (var placement in group)
                    {
                        var id = placement.GetProperty("id").GetString()!;
                        if (furnishings.TryGetValue(id, out var pose) && !placement.GetProperty("movable").GetBoolean())
                            throw new InvalidOperationException("Replicated overlay names a fixed ship object.");
                        if (pose.Deleted) continue;
                        var transform = ReadTransform(placement.GetProperty("matrix"));
                        if (pose != default)
                        {
                            var pivot = Vector(placement.GetProperty("pivot"));
                            // Prefab +X/+Y projects to renderer -Z/-X. Rotation remains a
                            // right-handed +Y yaw; apply around the same immutable authority pivot.
                            var rotate = new Basis(Vector3.Up, (float)pose.Yaw);
                            transform = new Transform3D(rotate * transform.Basis,
                                pivot + rotate * (transform.Origin - pivot) + new Vector3(-(float)pose.Y, 0, -(float)pose.X));
                        }
                        transforms.Add(transform);
                        entries.Add(placement);
                    }
                    AddMeshes(selected, SelectedAncestors(selected, prototype), transforms, entries, first);
                    var view = first.GetProperty("view").GetString()!;
                    placementCounts[view] = placementCounts.GetValueOrDefault(view) + transforms.Count;
                    loaded.Add(file);
                }
                finally { prototype.Free(); }
            }
            catch (Exception error)
            {
                missing.Add(first.GetProperty("piece").GetString()!);
                GD.PushWarning($"Native ship asset unavailable: {file} ({error.GetType().Name}).");
            }
        }
        if (!exteriorOnly) lightSockets.AddRange(ship.GetProperty("lights").EnumerateArray());
        SetCutaway(cutaway);
    }

    private static Node? FindNode(Node node, string name)
    {
        if (node.Name.ToString() == name || node.Name.ToString() == name.Replace('.', '_')) return node;
        foreach (var child in node.GetChildren())
            if (FindNode(child, name) is { } found) return found;
        return null;
    }

    private static Transform3D SelectedAncestors(Node selected, Node root)
    {
        if (selected == root) return Transform3D.Identity;
        var chain = new Stack<Node>();
        for (var parent = selected.GetParent(); parent != null; parent = parent.GetParent())
        {
            chain.Push(parent);
            if (parent == root) break;
        }
        var result = Transform3D.Identity;
        foreach (var parent in chain)
            if (parent is Node3D spatial) result *= spatial.Transform;
        return result;
    }

    private void AddMeshes(Node source, Transform3D parent, IReadOnlyList<Transform3D> placements, IReadOnlyList<JsonElement> entries, JsonElement entry)
    {
        var local = parent * (source is Node3D spatial ? spatial.Transform : Transform3D.Identity);
        if (source is MeshInstance3D { Mesh: not null } mesh)
        {
            // Clone the mesh resource only to apply engine-specific finish/material pooling.
            // Authored vertex positions, normals, UVs, embedded images and pivots stay intact.
            var modified = entries.Any(p => p.TryGetProperty("clipPlanes", out var planes) && planes.GetArrayLength() != 0 ||
                p.TryGetProperty("verticalProfile", out var profile) && profile.ValueKind != JsonValueKind.Null);
            if (modified)
            {
                var baked = BakeMeshes(mesh, local, placements, entries, entry);
                if (baked.GetSurfaceCount() != 0)
                {
                    foreach (var cohort in ReceiverMeshes(baked))
                    {
                        var clippedBatch = new MeshInstance3D { Name = "BrowserAuthoredProfileBatch", Mesh = cohort.Mesh };
                        SourceLightUnits.SetReceiver(clippedBatch,cohort.Class,layers);
                        AddChild(clippedBatch); banks.Add((clippedBatch, entry.GetProperty("view").GetString()!, OcclusionSide(entry)));
                    }
                    var aabb = baked.GetAabb(); bounds = hasBounds ? bounds.Merge(aabb) : aabb; hasBounds = true;
                }
                foreach (var child in source.GetChildren()) AddMeshes(child, local, placements, entries, entry);
                return;
            }
            var geometry = (Mesh)mesh.Mesh.Duplicate();
            for (var index = 0; index < geometry.GetSurfaceCount(); index++)
            {
                var original = mesh.GetActiveMaterial(index);
                geometry.SurfaceSetMaterial(index, AdaptMaterial(original, entry));
            }
            for (var index = 0; index < placements.Count; index++)
            {
                var transform = placements[index] * local;
                var aabb = transform * mesh.GetAabb();
                bounds = hasBounds ? bounds.Merge(aabb) : aabb; hasBounds = true;
                RecordFramingBounds(aabb, entry);
                if(!exteriorOnly)for (var surface=0;surface<mesh.Mesh.GetSurfaceCount();surface++) {
                    var pin=$"{mesh.Mesh.GetInstanceId()}:{surface}";
                    if(!pickChannels.TryGetValue(pin,out var channel)){var arrays=mesh.Mesh.SurfaceGetArrays(surface);var vertices=arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();var indices=arrays[(int)Mesh.ArrayType.Index].VariantType==Variant.Type.Nil?Enumerable.Range(0,vertices.Length).ToArray():arrays[(int)Mesh.ArrayType.Index].AsInt32Array();channel=(vertices,indices);pickChannels[pin]=channel;}
                    RecordPick(entries[index],transform,channel.Vertices,channel.Indices,aabb);
                }
            }
            foreach (var cohort in ReceiverMeshes(geometry))
            {
                var multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = cohort.Mesh, InstanceCount = placements.Count };
                for (var index=0;index<placements.Count;index++) multimesh.SetInstanceTransform(index,placements[index]*local);
                var batch = new MultiMeshInstance3D { Name = "PublishedMeshBatch", Multimesh = multimesh };
                SourceLightUnits.SetReceiver(batch,cohort.Class,layers);
                AddChild(batch); banks.Add((batch, entry.GetProperty("view").GetString()!, OcclusionSide(entry)));
            }
        }
        foreach (var child in source.GetChildren()) AddMeshes(child, local, placements, entries, entry);
    }

    // Light masks apply to geometry instances, not individual surfaces. Split only
    // mixed response banks; every source vertex/index/material channel is copied intact.
    private static IEnumerable<(Mesh Mesh,SourceLightClass Class)> ReceiverMeshes(Mesh source)
    {
        SourceLightClass Class(int surface) => source.SurfaceGetMaterial(surface) is { } material &&
            material.GetMeta("source_direct_intensity",SourceLightUnitsRules.HullDirect).AsDouble()==SourceLightUnitsRules.InteriorDirect
                ? SourceLightClass.Interior : SourceLightClass.Hull;
        var groups=Enumerable.Range(0,source.GetSurfaceCount()).GroupBy(Class).ToArray();
        if(groups.Length==1){yield return(source,groups[0].Key);yield break;}
        foreach(var group in groups)
        {
            var mesh=new ArrayMesh();
            foreach(var surface in group)
            {
                var primitive=source is ArrayMesh array?array.SurfaceGetPrimitiveType(surface):Mesh.PrimitiveType.Triangles;
                mesh.AddSurfaceFromArrays(primitive,source.SurfaceGetArrays(surface));
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount()-1,source.SurfaceGetMaterial(surface));
            }
            yield return(mesh,group.Key);
        }
    }

    private ArrayMesh BakeMeshes(MeshInstance3D source, Transform3D local, IReadOnlyList<Transform3D> placements,
        IReadOnlyList<JsonElement> entries, JsonElement first)
    {
        var result = new ArrayMesh();
        var origin = ship.GetProperty("geometryOrigin").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        for (var surface = 0; surface < source.Mesh!.GetSurfaceCount(); surface++)
        {
            var arrays = source.Mesh.SurfaceGetArrays(surface);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            if (vertices.Length != normals.Length) throw new InvalidOperationException("Authored mesh lost normals.");
            float[] Flat3(Vector3[] values) => values.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray();
            float[]? Flat2(Mesh.ArrayType type) => arrays[(int)type].VariantType == Variant.Type.Nil ? null :
                arrays[(int)type].AsVector2Array().SelectMany(v => new[] { v.X, v.Y }).ToArray();
            var indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? Enumerable.Range(0, vertices.Length).ToArray() : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            var native = new ReplicatedWorldSurface.Channels(Flat3(vertices), Flat3(normals), indices,
                Flat2(Mesh.ArrayType.TexUV), Flat2(Mesh.ArrayType.TexUV2),
                arrays[(int)Mesh.ArrayType.Tangent].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array());
            var positions = new List<float>(); var outputNormals = new List<float>(); var outputIndices = new List<int>();
            var uvs = native.Uvs == null ? null : new List<float>(); var uvs2 = native.Uvs2 == null ? null : new List<float>();
            var tangents = native.Tangents == null ? null : new List<float>();
            for (var index = 0; index < placements.Count; index++)
            {
                var placement = entries[index]; var transform = placements[index] * local;
                var matrix = new[] { transform.Basis.X.X, transform.Basis.X.Y, transform.Basis.X.Z, 0f,
                    transform.Basis.Y.X, transform.Basis.Y.Y, transform.Basis.Y.Z, 0f,
                    transform.Basis.Z.X, transform.Basis.Z.Y, transform.Basis.Z.Z, 0f,
                    transform.Origin.X, transform.Origin.Y, transform.Origin.Z, 1f };
                var clipping = placement.TryGetProperty("clipPlanes", out var planes) ?
                    planes.EnumerateArray().Select(p => p.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray() : Array.Empty<double[]>();
                var profile = placement.TryGetProperty("verticalProfile", out var p) && p.ValueKind == JsonValueKind.Object ?
                    new[] { p.GetProperty("bottom"), p.GetProperty("top") }.Select(row => row.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray() : null;
                var clipped = ReplicatedWorldSurface.Transform(native, matrix, clipping, profile, origin);
                if (clipped.Positions.Length != 0)
                {
                    var minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                    var maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                    for (var vertex = 0; vertex < clipped.Positions.Length; vertex += 3)
                    {
                        var point = new Vector3(clipped.Positions[vertex], clipped.Positions[vertex + 1], clipped.Positions[vertex + 2]);
                        minimum = minimum.Min(point); maximum = maximum.Max(point);
                    }
                    RecordFramingBounds(new Aabb(minimum, maximum - minimum), placement);
                    if(!exteriorOnly){var points=Enumerable.Range(0,clipped.Positions.Length/3).Select(i=>new Vector3(clipped.Positions[i*3],clipped.Positions[i*3+1],clipped.Positions[i*3+2])).ToArray();RecordPick(placement,Transform3D.Identity,points,clipped.Indices,new Aabb(minimum,maximum-minimum));}
                }
                var offset = positions.Count / 3;
                positions.AddRange(clipped.Positions); outputNormals.AddRange(clipped.Normals);
                outputIndices.AddRange(clipped.Indices.Select(i => i + offset));
                if (clipped.Uvs != null) uvs!.AddRange(clipped.Uvs);
                if (clipped.Uvs2 != null) uvs2!.AddRange(clipped.Uvs2);
                if (clipped.Tangents != null) tangents!.AddRange(clipped.Tangents);
            }
            if (outputIndices.Count == 0) continue;
            Vector3[] Pack3(List<float> values) => Enumerable.Range(0, values.Count / 3).Select(i => new Vector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2])).ToArray();
            Vector2[] Pack2(List<float> values) => Enumerable.Range(0, values.Count / 2).Select(i => new Vector2(values[i * 2], values[i * 2 + 1])).ToArray();
            var output = new Godot.Collections.Array(); output.Resize((int)Mesh.ArrayType.Max);
            output[(int)Mesh.ArrayType.Vertex] = Pack3(positions); output[(int)Mesh.ArrayType.Normal] = Pack3(outputNormals);
            output[(int)Mesh.ArrayType.Index] = outputIndices.ToArray();
            if (uvs != null) output[(int)Mesh.ArrayType.TexUV] = Pack2(uvs);
            if (uvs2 != null) output[(int)Mesh.ArrayType.TexUV2] = Pack2(uvs2);
            if (tangents != null) output[(int)Mesh.ArrayType.Tangent] = tangents.ToArray();
            result.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, output);
            result.SurfaceSetMaterial(result.GetSurfaceCount() - 1, AdaptMaterial(source.GetActiveMaterial(surface), first));
        }
        return result;
    }

    private void RecordFramingBounds(Aabb box, JsonElement entry) =>
        framingBounds.Add((box, entry.GetProperty("view").GetString()!, OcclusionSide(entry)));

    /// <summary>Camera-plane bounds of actual active geometry, retaining individual
    /// placement/surface boxes so empty aggregate corners and hidden roofs cannot enlarge it.</summary>
    public Rect2 GetFramingBounds(Transform3D world, Basis camera, Vector3 target)
    {
        var right = camera.X.Normalized(); var up = camera.Y.Normalized();
        var minimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var maximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        var found = false;
        foreach (var (box, view, side) in framingBounds)
        {
            if (view != "both" && view != (currentCutaway ? "deck" : "flight") || currentCutaway && side == foregroundSide && side.Length != 0) continue;
            for (var index = 0; index < 8; index++)
            {
                var relative = world * box.GetEndpoint(index) - target;
                var point = new Vector2(relative.Dot(right), relative.Dot(up));
                minimum = minimum.Min(point); maximum = maximum.Max(point); found = true;
            }
        }
        return found ? new Rect2(minimum, maximum - minimum) : default;
    }

    private Material? AdaptMaterial(Material? source, JsonElement entry)
    {
        if (source is not BaseMaterial3D original) return source;
        var authored = entry.GetProperty("authored").GetBoolean();
        var theme = ship.GetProperty("theme").GetString()!;
        var role = entry.GetProperty("role").GetString()!;
        var themed = entry.TryGetProperty("themeSlots", out var themeSlots) && themeSlots.GetBoolean();
        var key = $"{source.GetInstanceId()}:{theme}:{role}:{authored}:{themed}";
        if (materials.TryGetValue(key, out var found)) return found;
        var material = (BaseMaterial3D)original.Duplicate();
        material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        string? sourceFamily = null;
        var sourceName = original.ResourceName.Split('.')[0].ToLowerInvariant();
        if (sourceName.StartsWith("slot") && sourceName.Contains('_')) sourceName = sourceName[(sourceName.IndexOf('_') + 1)..];
        if (sourceName.StartsWith("slot:",StringComparison.Ordinal)) sourceName = sourceName[5..];
        sourceName = sourceName.Split('@')[0];
        if (authored)
        {
            var palette = ship.GetProperty("palette");
            if (palette.TryGetProperty(original.ResourceName,out var authoredPalette) && authoredPalette.TryGetProperty("family",out var family)) sourceFamily = family.GetString();
            var asset = ReplicatedWorld.Catalog.Root.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("file").GetString() == entry.GetProperty("file").GetString());
            if (asset.ValueKind != JsonValueKind.Undefined && asset.TryGetProperty("gameMaterials",out var definitions))
            {
                var definition = definitions.EnumerateArray().FirstOrDefault(m => m.GetProperty("name").GetString() == original.ResourceName);
                if (definition.ValueKind != JsonValueKind.Undefined)
                {
                    sourceFamily ??= definition.GetProperty("family").GetString();
                    if (material.EmissionEnabled) material.EmissionEnergyMultiplier = definition.GetProperty("emissionStrength").GetSingle();
                }
            }
            if (themed)
            {
                var name = original.ResourceName;
                var slotName = palette.TryGetProperty(name, out var paletteEntry) && paletteEntry.TryGetProperty("sourceSlotName", out var sourceSlot) ?
                    sourceSlot.GetString()! : name.Split('@')[0];
                sourceName = slotName;
                var slots = ReplicatedWorld.Catalog.Root.GetProperty("themes").GetProperty(theme).GetProperty("slots");
                if (slots.TryGetProperty(slotName, out var slot))
                {
                    var color = Colour(slot.GetProperty("colour"));
                    if (slotName is "primary" or "secondary" or "accent" or "trim" or "dark" or "metal") material.AlbedoColor = color;
                    else if (slotName is "emit_a" or "emit_b") { material.EmissionEnabled = true; material.Emission = color; }
                }
            }
        }
        else
        {
            var name = original.ResourceName.Split('.')[0].ToLowerInvariant();
            if (name.StartsWith("slot") && name.Contains('_')) name = name[(name.IndexOf('_') + 1)..];
            var slots = ReplicatedWorld.Catalog.Root.GetProperty("themes").GetProperty(theme).GetProperty("slots");
            if (slots.TryGetProperty(name, out var slot))
            {
                var color = Colour(slot.GetProperty("colour"));
                material.AlbedoColor = color; material.Roughness = slot.GetProperty("roughness").GetSingle();
                material.Metallic = slot.GetProperty("metallic").GetSingle();
                if (slot.TryGetProperty("emissive", out var emission))
                { material.EmissionEnabled = true; material.Emission = color; material.EmissionEnergyMultiplier = Math.Min(1.3f, emission.GetSingle() / 6.5f); }
                if (role == "floor") material.AlbedoColor = name switch
                {
                    "primary" => new Color(.64f, .637f, .64f), "secondary" => new Color(.396f, .407f, .436f),
                    "trim" => new Color(.556f, .556f, .57f), "dark" => new Color(.27f, .277f, .303f),
                    "metal" => new Color(.618f, .623f, .634f), _ => material.AlbedoColor,
                };
            }
        }
        SourceSurfaceFinish.Apply(material,sourceFamily == "plastic-deck" ? "plastic-dark" : sourceFamily ?? SourceSurfaceFinish.ShipFamily(sourceName));
        material.SetMeta("source_direct_intensity",SourceLightUnitsRules.ShipDirect(authored,role,sourceName));
        materials[key] = material; return material;
    }

    public void SetCutaway(bool cutaway)
    {
        currentCutaway = cutaway;
        foreach (var (node, view, _) in banks) node.Visible = view == "both" || view == (cutaway ? "deck" : "flight");
        RenderedPlacementCount = placementCounts.GetValueOrDefault("both") + placementCounts.GetValueOrDefault(cutaway ? "deck" : "flight");
    }

    public void ApplyPreferences(NativePreferencesSnapshot next)
    {
        preferences=next;
        foreach(var (node,_,_) in banks) if(node is GeometryInstance3D geometry)
            geometry.CastShadow=next.Shadows?GeometryInstance3D.ShadowCastingSetting.On:GeometryInstance3D.ShadowCastingSetting.Off;
        if (!next.Lighting || next.LocalLightLimit=="0") foreach(var light in lightPool)light.Visible=false;
    }
    public void UpdateLocalLights(Vector3 cameraLocal)
    {
        var budget=preferences.Lighting?(preferences.LocalLightLimit=="all"?32:int.Parse(preferences.LocalLightLimit)):0;
        // Compatibility supports eight practical lights per object. The pool still retains
        // exact authored sockets, choosing nearby visible sockets deterministically.
        if(RenderingServer.GetCurrentRenderingMethod()=="gl_compatibility")budget=Math.Min(budget,8);
        var selected=lightSockets.Where(l=>{var view=l.TryGetProperty("view",out var v)?v.GetString():"deck";return view=="both"||view==(currentCutaway?"deck":"flight");})
            .OrderBy(l=>Vector(l.GetProperty("at")).DistanceSquaredTo(cameraLocal)).ThenBy(l=>l.TryGetProperty("id",out var id)?id.GetString():"",StringComparer.Ordinal).Take(budget).ToArray();
        // Two mutually exclusive ship receiver responses per authored socket.
        // Logical socket budgets remain32/8; each object still receives at most that
        // many native practical lights. Source point falloff/owner-only receivers
        // require a separate shader/batching port and are not claimed by this adapter.
        var cohorts=new[]{SourceLightClass.Hull,SourceLightClass.Interior};
        while(lightPool.Count<selected.Length*cohorts.Length) {var light=new OmniLight3D{Name="PublishedPracticalPool",ShadowEnabled=false,LightSpecular=0};AddChild(light);lightPool.Add(light);}
        for(var i=0;i<lightPool.Count;i++) {
            var light=lightPool[i];light.Visible=i<selected.Length*cohorts.Length;if(!light.Visible)continue;
            var socket=selected[i/cohorts.Length];var cohort=cohorts[i%cohorts.Length];var colour=socket.GetProperty("colour");
            SourceLightUnits.Apply(light,socket.GetProperty("energy").GetDouble(),new Color(colour[0].GetSingle(),colour[1].GetSingle(),colour[2].GetSingle()),SourceLightUnits.Direct(cohort),SourceLightUnits.Cohort(cohort),layers);
            light.Position=Vector(socket.GetProperty("at"));light.OmniRange=socket.GetProperty("range").GetSingle();
        }
    }
    private void RecordPick(JsonElement entry,Transform3D transform,Vector3[] points,int[] indices,Aabb bounds) { if(!exteriorOnly)pickGeometry.Add((entry.GetProperty("id").GetString()!,entry.GetProperty("role").GetString()!,transform,points,indices,bounds,entry.GetProperty("view").GetString()!,OcclusionSide(entry))); }
    public Hit? Pick(Vector3 localOrigin,Vector3 localDirection)
    {
        Hit? nearest=null;
        foreach(var g in pickGeometry) {
            if(g.View!="both"&&g.View!=(currentCutaway?"deck":"flight")||currentCutaway&&g.Side.Length!=0&&g.Side==foregroundSide||!g.Bounds.IntersectsSegment(localOrigin,localOrigin+localDirection*100000))continue;
            var inverse=g.Transform.AffineInverse();var origin=inverse*localOrigin;var direction=inverse.Basis*localDirection;
            for(var i=0;i+2<g.Indices.Length;i+=3) {var a=g.Vertices[g.Indices[i]];var b=g.Vertices[g.Indices[i+1]];var c=g.Vertices[g.Indices[i+2]];var e1=b-a;var e2=c-a;var p=direction.Cross(e2);var determinant=e1.Dot(p);if(Math.Abs(determinant)<1e-8)continue;var t=origin-a;var u=t.Dot(p)/determinant;if(u<0||u>1)continue;var q=t.Cross(e1);var v=direction.Dot(q)/determinant;if(v<0||u+v>1)continue;var distance=e2.Dot(q)/determinant;if(distance<0)continue;var point=g.Transform*(origin+direction*distance);var actual=point.DistanceTo(localOrigin);if(nearest==null||actual<nearest.Value.Distance)nearest=new Hit(g.Id,g.Role,point,actual);}
        }
        return nearest;
    }

    private static string OcclusionSide(JsonElement placement)
    {
        var id = placement.GetProperty("id").GetString()!;
        return id.StartsWith("WALL_near_", StringComparison.Ordinal) || id.StartsWith("LINER_near_", StringComparison.Ordinal) ? "near" :
            id.StartsWith("WALL_far_", StringComparison.Ordinal) || id.StartsWith("LINER_far_", StringComparison.Ordinal) ? "far" : "";
    }

    /// <summary>Hide only camera-facing architectural liners in the authored deck cohort.</summary>
    public void UpdateCutaway(Vector3 cameraInAssemblySpace)
    {
        var foreground = cameraInAssemblySpace.X < 0 ? "near" : "far";
        foregroundSide = foreground;
        foreach (var (node, view, side) in banks)
            node.Visible = (view == "both" || view == (currentCutaway ? "deck" : "flight")) &&
                !(currentCutaway && side == foreground);
    }

    public static Transform3D ReadTransform(JsonElement values) => new(
        new Basis(new Vector3(values[0].GetSingle(), values[1].GetSingle(), values[2].GetSingle()),
            new Vector3(values[4].GetSingle(), values[5].GetSingle(), values[6].GetSingle()),
            new Vector3(values[8].GetSingle(), values[9].GetSingle(), values[10].GetSingle())),
        new Vector3(values[12].GetSingle(), values[13].GetSingle(), values[14].GetSingle()));
    private static Vector3 Vector(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
    // The authored descriptors/themes use linear RGB. Godot's material and light
    // color properties use the same sRGB convention as its glTF importer.
    private static Color Colour(JsonElement value) => new Color(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle()).LinearToSrgb();
}
