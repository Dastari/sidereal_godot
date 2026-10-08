using Godot;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Sidereal.Native;
using Sidereal.Bindings;

/// <summary>Disclosed current-deck drops. Placement, discovery and pickup stay authoritative.</summary>
public partial class SpaceGroundItems : Node3D
{
    private sealed class Entry { public Node3D Root=null!;public Node3D? Mesh;public string? File; }
    private readonly Dictionary<string,Entry> entries=new();
    private readonly Dictionary<string,PackedScene> ready=new();
    private readonly HashSet<string> requested=new(),missing=new();
    private string? context;
    public int ReadyCount=>entries.Values.Count(e=>e.Mesh!=null);
    public string[] MissingAssetPins=>missing.ToArray();
    public static VisibleGroundItem[] ReadRows(ClientCore core,bool interior)
    {
        var connection=core.Connection;var location=core.Location;
        if(!interior||connection==null||core.Eva!=null||location==null||core.Instance?.Id!=location.InstanceId||connection.Db.OwnConstructionStairWalks.Iter().Any(r=>r.CharacterId==core.Character?.Id))return Array.Empty<VisibleGroundItem>();
        return connection.Db.OwnGroundItems.Iter().Where(r=>r.InstanceId==location.InstanceId&&r.DeckId==location.DeckId&&double.IsFinite(r.LocalX+r.LocalY+r.ElevationM)).ToArray();
    }
    public void Clear(){foreach(var e in entries.Values){e.Root.Visible=false;e.Root.QueueFree();}entries.Clear();missing.Clear();}
    public void Sync(ClientCore core,Node3D shipRoot,bool interior,uint layers,NativePreferencesSnapshot preferences)
    {
        var next=core.Connection==null?null:$"{core.Connection.GetHashCode()}:{core.Character?.Id}:{core.SharedWorldEpoch}:{core.Location?.InstanceId}:{core.Location?.DeckId}";
        if(next!=context){context=next;Clear();}
        var rows=ReadRows(core,interior);var ids=rows.Select(r=>r.Id).ToHashSet();
        foreach(var id in entries.Keys.Where(id=>!ids.Contains(id)).ToArray()){entries[id].Root.Visible=false;entries[id].Root.QueueFree();entries.Remove(id);}
        foreach(var row in rows)
        {
            var pin=core.Connection!.Db.OwnItemDefinitionPins.Iter().FirstOrDefault(p=>p.ItemId==row.Id&&p.DefinitionId==row.DefinitionId);
            var source=pin?.ItemRevision>1?default:ReplicatedWorld.Catalog.Root.GetProperty("groundItems").EnumerateArray().FirstOrDefault(d=>d.GetProperty("definitionId").GetString()==row.DefinitionId);
            var file=source.ValueKind==JsonValueKind.Undefined?null:source.GetProperty("file").GetString();
            if(!entries.TryGetValue(row.Id,out var entry)) {entry=new(){Root=new Node3D{Name="AcceptedGroundItem"}};entry.Root.SetMeta("ground_item_id",row.Id);shipRoot.AddChild(entry.Root);entries[row.Id]=entry;}
            entry.Root.Position=new((float)row.LocalX,(float)row.ElevationM,-(float)row.LocalY);entry.Root.Visible=true;
            if(entry.File!=file){entry.Mesh?.QueueFree();entry.Mesh=null;entry.File=file;}
            if(file!=null&&entry.Mesh==null&&Load(file) is { } scene)
            {
                var placement=new Node3D{Name="SourceGroundSurface"};var model=scene.Instantiate<Node3D>();placement.AddChild(model);
                if(source.GetProperty("layDown").GetBoolean())placement.Rotation=new((float)(Math.PI/2),0,0);
                var bounds=Bounds(placement);if(bounds is { } box)placement.Position=new(-(box.Position.X+box.End.X)/2,-box.Position.Y,-(box.Position.Z+box.End.Z)/2);
                entry.Root.AddChild(placement);entry.Mesh=placement;
            }
            if(entry.Mesh!=null)Apply(entry.Mesh,layers,preferences.Shadows);
        }
        // Finish background requests even when their actor-scoped drop disappeared.
        foreach(var file in requested.ToArray()) {var status=ResourceLoader.LoadThreadedGetStatus("res://Assets/World/"+file);if(status==ResourceLoader.ThreadLoadStatus.InProgress)continue;if(status==ResourceLoader.ThreadLoadStatus.Loaded&&ResourceLoader.LoadThreadedGet("res://Assets/World/"+file) is PackedScene scene)ready[file]=scene;else missing.Add(file);requested.Remove(file);}
    }
    private PackedScene? Load(string file)
    {
        if(ready.TryGetValue(file,out var scene))return scene;if(missing.Contains(file))return null;
        var path="res://Assets/World/"+file;
        if(!requested.Contains(file)){if(!ResourceLoader.Exists(path)||ResourceLoader.LoadThreadedRequest(path,"PackedScene")!=Error.Ok){missing.Add(file);return null;}requested.Add(file);}return null;
    }
    private static Aabb? Bounds(Node3D root)
    {
        Aabb? bounds=null;
        void Visit(Node node,Transform3D parent){var transform=node is Node3D spatial?parent*spatial.Transform:parent;if(node is MeshInstance3D mesh&&mesh.Mesh!=null){var box=transform*mesh.Mesh.GetAabb();bounds=bounds is { } old?old.Merge(box):box;}foreach(var child in node.GetChildren())Visit(child,transform);}
        Visit(root,Transform3D.Identity);return bounds;
    }
    private static void Apply(Node node,uint layers,bool shadows){if(node is GeometryInstance3D mesh){SourceLightUnits.SetReceiver(mesh,SourceLightClass.ReferenceSurface,layers,false);mesh.CastShadow=shadows?GeometryInstance3D.ShadowCastingSetting.On:GeometryInstance3D.ShadowCastingSetting.Off;}foreach(var child in node.GetChildren())Apply(child,layers,shadows);}
}
