using Godot;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
namespace Sidereal.Native;

/// <summary>Accepted server crew rows only. All world coordinates subtract the render origin as doubles.</summary>
public partial class CrewPresenter:Node3D
{
    private sealed class Entry
    {
        public CrewModel Model=null!;public CrewMotionReplay Replay=new();public double Yaw;public Label3D? Label;
    }
    private readonly Dictionary<string,Entry> entries=new();
    private Node3D? shipRoot;private double originX,originY,elevation;private bool interior;private uint layers=1;private ulong epoch;
    public bool ReducedMotion {get;set;}
    public bool HasReady(string characterId)=>entries.TryGetValue(characterId,out var entry)&&entry.Model.Ready&&entry.Model.Visible;
    public CombatBodyAnchor? CombatAnchor(string characterId)
    {
        if(!entries.TryGetValue(characterId,out var entry)||!entry.Model.Ready||!entry.Model.IsVisibleInTree())return null;
        var position=entry.Model.GlobalPosition;
        return new(entry.Model.Position.Y,BodyRenderPosition:new(position.X,position.Y,position.Z));
    }
    public int ReadyCount=>entries.Values.Count(x=>x.Model.Ready);
    public int PendingCount=>entries.Values.Sum(x=>x.Model.PendingCount);
    public int UnsupportedCount=>entries.Values.Sum(x=>x.Model.Unsupported.Length);
    public string[] MissingAssets=>entries.Values.SelectMany(x=>x.Model.Unsupported).Distinct().ToArray();
    public void ConfigureFrame(Node3D presentedShipRoot,double renderOriginX,double renderOriginY,double standingElevation,uint renderLayers,bool interiorVisible)
    {shipRoot=presentedShipRoot;originX=renderOriginX;originY=renderOriginY;elevation=standingElevation;layers=renderLayers;interior=interiorVisible;}
    public static CrewModel CreatePreview(ClientCore core,uint layers=1)
    {var model=new CrewModel{Name="ReleasedCrewPreview",RenderLayers=layers};model.SetAppearance(CrewAppearanceView.FromCore(core));return model;}
    public static CrewModel CreatePreview(string appearanceJson,InventorySnapshot inventory,uint layers=1)
    {
        var worn=inventory.Items.Where(i=>i.Definition!=null&&i.EquipmentSlot.Length>0&&i.EquipmentSlot!="hand").ToDictionary(i=>i.EquipmentSlot,i=>i.DefinitionId);
        var item=inventory.Items.FirstOrDefault(i=>i.EquipmentSlot=="hand"&&i.Definition!=null);var model=new CrewModel{Name="ReleasedCrewPreview",RenderLayers=layers};model.SetAppearance(new(CrewAssets.Catalog.Resolve(appearanceJson,worn,item==null?null:CrewAssets.Catalog.DefinitionHeld(item.DefinitionId))));return model;
    }
    public void Clear()
    {foreach(var e in entries.Values){e.Model.Visible=false;e.Model.QueueFree();}entries.Clear();}
    public void Sync(ClientCore core,double elapsed)
    {
        // The world may freeze its cosmetic clock for accessibility. Accepted actor
        // samples still require real ordered timestamps while motion clips freeze.
        elapsed=Time.GetTicksMsec()/1000d;
        if(epoch!=core.SharedWorldEpoch){Clear();epoch=core.SharedWorldEpoch;}
        var connection=core.Connection;var actor=core.Character;if(connection==null||actor==null||!core.SharedAdmissionReady||shipRoot==null){Clear();return;}
        var keep=new HashSet<string>();var deck=core.Location?.DeckId??core.PassengerInterior?.DeckId;var presented=core.CurrentPresentedShip;
        if(core.Eva is { } eva&&core.SpatialReady)
        {
            var cycling=connection.Db.OwnEvaAirlockCycle.Iter().Any(r=>r.CharacterId==actor.Id);
            var motion=new CrewMotionState(Aiming:core.Combat?.AimActive==true,Dead:!core.Alive,ShotSequence:core.Combat?.ShotSequence??0,Eva:true,EvaPhase:"free",Forward:eva.Forward,Strafe:eva.Strafe,Turn:eva.Turn,Cycling:cycling);
            var yaw=core.Combat?.AimActive==true?-core.Combat.AimAngle:eva.Heading;
            Draw(actor.Id,CrewAppearanceView.FromCore(core),motion,elapsed,NodeForWorld(),eva.X-originX,-(eva.Y-originY),elevation-(cycling?0:.3125),yaw,false,keep);
        }
        else if(core.Eva==null&&interior&&core.Location!=null&&core.Instance?.Id==core.Location.InstanceId)
        {
            var action=connection.Db.VisibleCombatActions.Iter().FirstOrDefault(r=>r.CharacterId==actor.Id);
            var seated=core.Resting||core.IsPiloting;var x=core.Seat?.LocalX??actor.LocalX;var y=core.Seat?.LocalY??actor.LocalY;var contact=seated?Seat(core,x,y):null;
            var motion=new CrewMotionState(Sprinting:actor.Sprinting,Seated:seated,Dead:!core.Alive,Aiming:core.Combat?.AimActive==true,ShotSequence:core.Combat?.ShotSequence??0,ReloadSequence:action?.ReloadSequence??0);
            Draw(actor.Id,CrewAppearanceView.FromCore(core),motion,elapsed,shipRoot,x,-y,(core.Seat?.StandingElevationM??core.Location.StandingElevationM)+(contact?.Lift??0),contact?.Facing??(seated?Math.Sign(x)*Math.PI/2:core.Combat?.AimActive==true?-core.Combat.AimAngle:double.NaN),true,keep,contact);
        }
        if(interior&&deck!=null&&presented!=null)
        {
            var looks=connection.Db.VisibleCrewPresentation.Iter().ToDictionary(r=>r.CharacterId);
            foreach(var row in connection.Db.CurrentInteriorCrew.Iter())
            {
                if(row.CharacterId==actor.Id||row.ShipId!=presented.Id||row.DeckId!=deck||!looks.TryGetValue(row.CharacterId,out var look)||look.ShipId!=row.ShipId||look.DeckId!=row.DeckId)continue;
                var action=connection.Db.VisibleCombatActions.Iter().FirstOrDefault(a=>a.CharacterId==row.CharacterId&&a.ShipId==row.ShipId&&a.DeckId==row.DeckId);var contact=look.Seated?Seat(core,row.LocalX,row.LocalY):null;
                var motion=new CrewMotionState(Sprinting:row.Sprinting,Seated:look.Seated,Dead:look.Dead,Aiming:look.AimActive,ShotSequence:look.ShotSequence,ReloadSequence:action?.ReloadSequence??0);
                Draw(row.CharacterId,CrewAppearanceView.FromRows(look.AppearanceJson,look.EquipmentJson),motion,elapsed,shipRoot,row.LocalX,-row.LocalY,row.StandingElevationM+(contact?.Lift??0),contact?.Facing??(look.Seated?Math.Sign(row.LocalX)*Math.PI/2:look.AimActive?-look.AimAngle:double.NaN),true,keep,contact,row.Name,row.Connected);
            }
        }
        if(core.SpatialReady)
        foreach(var row in connection.Db.VisibleEvaBodies.Iter())
        {
            if(row.CharacterId==actor.Id)continue;
            var admission=connection.Db.OwnWorldAdmission.Iter().FirstOrDefault(r=>r.CharacterId==actor.Id);if(admission==null||row.SystemId!=admission.SystemId)continue;
            var motion=new CrewMotionState(Dead:row.Dead,Aiming:row.AimActive,ShotSequence:row.ShotSequence,Eva:true,EvaPhase:"free",Forward:row.Forward,Strafe:row.Strafe,Turn:row.Turn,Walking:row.Walking,Cycling:row.Cycling);
            Draw(row.CharacterId,CrewAppearanceView.FromRows(row.AppearanceJson,row.EquipmentJson),motion,elapsed,NodeForWorld(),row.X-originX,-(row.Y-originY),elevation-(row.Cycling?0:.3125),row.AimActive?-row.AimAngle:row.Heading,false,keep,name:row.Name,connected:row.Connected);
        }
        foreach(var id in entries.Keys.Where(id=>!keep.Contains(id)).ToArray()){entries[id].Model.Visible=false;entries[id].Model.QueueFree();entries.Remove(id);}
    }
    private Node3D NodeForWorld()=>this;
    private void Draw(string id,CrewAppearanceView appearance,CrewMotionState motion,double elapsed,Node3D parent,double x,double z,double height,double yaw,bool walking,HashSet<string> keep,CrewSeatContact? contact=null,string? name=null,bool connected=true)
    {
        if(!double.IsFinite(x)||!double.IsFinite(z)||!double.IsFinite(height))return;keep.Add(id);
        if(!entries.TryGetValue(id,out var entry)){entry=new(){Model=new CrewModel{Name="Crew_"+id,RenderLayers=layers}};parent.AddChild(entry.Model);entries.Add(id,entry);}
        else if(entry.Model.GetParent()!=parent)entry.Model.Reparent(parent,false);
        var displayedX=x;var displayedZ=z;var displayedHeight=height;var speed=0d;var moving=false;
        if(walking)
        {
            entry.Replay.Push(elapsed,x,-z,height);var shown=entry.Replay.Read(elapsed,motion.Seated,motion.Dead,motion.Aiming,!double.IsNaN(yaw)?-yaw:0,!double.IsNaN(yaw)?yaw:Math.Sign(x)*Math.PI/2);
            displayedX=shown.X;displayedZ=-shown.Y;displayedHeight=shown.Height;entry.Yaw=shown.Yaw;speed=shown.Speed;moving=shown.Moving;
        }
        else if(!double.IsNaN(yaw))entry.Yaw=yaw;
        entry.Model.Position=new((float)displayedX,(float)displayedHeight,(float)displayedZ);entry.Model.Rotation=new(0,(float)entry.Yaw,0);entry.Model.Visible=true;entry.Model.SetAppearance(appearance);entry.Model.SetSeatContact(contact);
        entry.Model.ReducedMotion=ReducedMotion;entry.Model.Tick(motion with{Moving=moving,SpeedMultiplier=walking?Math.Clamp(speed/(motion.Sprinting?CrewAssets.Catalog.Semantics.GetProperty("sprintSpeed").GetDouble():CrewAssets.Catalog.Semantics.GetProperty("walkSpeed").GetDouble()),.1,2):1},elapsed);
        if(name!=null)
        {
            if(entry.Label==null)
            {
                entry.Label=new Label3D{Name="AcceptedCrewName",Position=new(0,2.15f,0),FontSize=38,PixelSize=1.4f/512,OutlineSize=8,OutlineModulate=new Color(8/255f,14/255f,24/255f,.62f),Billboard=BaseMaterial3D.BillboardModeEnum.Enabled,Shaded=false,Layers=layers};entry.Model.AddChild(entry.Label);
            }
            entry.Label.Text=name.Length>22?name[..21]+"…":name;entry.Label.Modulate=Color.FromString(motion.Dead?"#ff8f8f":connected?"#e8f1ff":"#8b98ab",Colors.White);entry.Label.Visible=entry.Model.Ready;
        }
    }
    private CrewSeatContact? Seat(ClientCore core,double x,double y)
    {
        if(core.Instance==null)return null;try
        {
            var ship=ReplicatedWorld.Catalog.Match(core.Instance.DocumentJson,core.Location?.DeckId);var hash=ship.GetProperty("prefabSha256").GetString();var found=CrewAssets.Catalog.Semantics.GetProperty("seats").EnumerateArray().FirstOrDefault(r=>r.GetProperty("prefabSha256").GetString()==hash);
            if(found.ValueKind==JsonValueKind.Undefined)return null;
            using var furnishing=JsonDocument.Parse(string.IsNullOrWhiteSpace(core.Instance.FurnishingsJson)?"{}":core.Instance.FurnishingsJson);
            foreach(var row in found.GetProperty("points").EnumerateArray())
            {
                var px=row.GetProperty("x").GetDouble();var py=row.GetProperty("y").GetDouble();var yaw=0d;
                var objectId=CrewCatalog.Text(row,"objectId");
                if(objectId.Length>0&&furnishing.RootElement.TryGetProperty(objectId,out var f))
                {
                    if(f.TryGetProperty("deleted",out var deleted)&&deleted.GetBoolean())continue;
                    double F(string key)=>f.TryGetProperty(key,out var v)?v.GetDouble():0;
                    yaw=F("yaw");var cx=row.GetProperty("centerX").GetDouble();var cy=row.GetProperty("centerY").GetDouble();var dx=px-cx;var dy=py-cy;
                    double Round(double value)=>Math.Floor(value*1e6+.5)/1e6;
                    px=Round(cx+Math.Cos(yaw)*dx-Math.Sin(yaw)*dy-F("dy"));py=Round(cy+Math.Sin(yaw)*dx+Math.Cos(yaw)*dy+F("dx"));
                }
                if(Math.Abs(px-x)>=1e-4||Math.Abs(py-y)>=1e-4)continue;
                var c=row.GetProperty("contact");double N(string k)=>c.TryGetProperty(k,out var v)?v.GetDouble():0;return new(N("facing")+yaw,N("lift"),N("lean"),N("footSupport"),N("forward"),c.TryGetProperty("footForward",out var ff)?ff.GetDouble():null);
            }
        }
        catch(Exception e)when(e is JsonException or InvalidOperationException or KeyNotFoundException){}return null;
    }
}
public sealed record CrewSeatContact(double Facing,double Lift,double Lean,double FootSupport,double Forward,double? FootForward);
