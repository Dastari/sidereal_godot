using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using Sidereal.Native;

namespace Sidereal.Ui;

/// <summary>Inspector over the active authorized construction, never a remote ship document.</summary>
public partial class ObjectDetailsWindow : DockWindow
{
    private readonly ClientCore core;
    private readonly Label name, description, stats, result;
    private readonly Button interact, power;
    private readonly VBoxContainer furnishing;
    private string selected="", sourceId="", cacheKey="";
    private JsonElement placement;
    private bool movable;
    public ObjectDetailsWindow(ClientCore core):base("object-details","Object details",new Vector2(900,110),new Vector2(390,500))
    {
        this.core=core;name=UiKit.Heading("Selected object",23);name.AutowrapMode=TextServer.AutowrapMode.WordSmart;Content.AddChild(name);
        description=UiKit.Paragraph("",0).Sized(14);Content.AddChild(description);stats=UiKit.Paragraph("",0).Sized(14);Content.AddChild(stats);
        interact=UiKit.Button("Use",()=>{GetViewport().GuiReleaseFocus();core.InteractSelected(selected,fromUi:true);});Content.AddChild(interact);
        power=UiKit.Button("Connect",TogglePower);Content.AddChild(power);
        furnishing=new VBoxContainer();Content.AddChild(furnishing);furnishing.AddChild(UiKit.Label("Move furnishing · 0.25 m",14));
        var row=new GridContainer {Columns=2,SizeFlagsHorizontal=SizeFlags.ExpandFill};furnishing.AddChild(row);
        foreach(var (label,dx,dy) in new[]{("Port",-.25,0d),("Starboard",.25,0d),("Forward",0d,.25),("Aft",0d,-.25)})
        {var moveX=dx;var moveY=dy;row.AddChild(UiKit.Button(label,()=>Edit(moveX,moveY,0,"move")));}
        furnishing.AddChild(UiKit.Button("Rotate 90°",()=>Edit(0,0,Math.PI/2,"move")));
        furnishing.AddChild(UiKit.Button("Snap to grid",()=>Edit(0,0,0,"snap")));
        result=UiKit.Paragraph("",0).Sized(13);Content.AddChild(result);
        Closed+=()=>{core.SelectedPlacementId=null;selected="";};
    }
    public void Inspect(string? id)
    {
        selected=id??"";core.SelectedPlacementId=id;cacheKey="";
        if(selected.Length==0){Hide();return;}Refresh();if(selected.Length>0){Show();BringToFront();}
    }
    private void ReadPlacement()
    {
        placement=default;sourceId=selected;movable=false;
        if(core.Instance is not {} instance)return;
        try
        {
            using var document=JsonDocument.Parse(instance.DocumentJson);
            if(document.RootElement.TryGetProperty("prefab",out var binding)&&binding.TryGetProperty("identities",out var identities))
                foreach(var identity in identities.EnumerateObject())if(identity.Value.GetString()==selected){sourceId=identity.Name;break;}
            var ship=ReplicatedWorld.Catalog.Match(instance.DocumentJson,core.Location?.DeckId);
            var lookup=sourceId.StartsWith(instance.Id+":",StringComparison.Ordinal)?sourceId[(instance.Id.Length+1)..]:sourceId;
            if(lookup.StartsWith("mount-",StringComparison.Ordinal))lookup="mount:"+lookup[6..];
            var found=ship.GetProperty("placements").EnumerateArray().FirstOrDefault(p=>p.GetProperty("id").GetString()==lookup);
            if(found.ValueKind!=JsonValueKind.Undefined){placement=found.Clone();sourceId=lookup;movable=found.TryGetProperty("movable",out var flag)&&flag.GetBoolean();}
        }
        catch(Exception exception) when(exception is JsonException or InvalidOperationException or KeyNotFoundException){placement=default;}
    }
    private void TogglePower()
    {
        if(!core.DevicePowerAvailable)return;
        var fitting=core.Connection?.Db.OwnAuthoredFlightPowerFittings.Iter().FirstOrDefault(f=>f.PlacedObjectId==selected);
        if(fitting==null)return;if(fitting.Kind=="computer")core.SetComputerPower(selected,!fitting.Powered);else if(fitting.Kind=="actuator")core.SetEnginePower(selected,!fitting.Powered);
    }
    private void Edit(double dx,double dy,double yaw,string action)
    {
        var current=new FurnishingPose();
        try{if(core.Instance is {} instance)current=ReplicatedWorldCatalog.ReadFurnishings(instance.FurnishingsJson).GetValueOrDefault(sourceId);}catch(Exception exception) when(exception is JsonException or InvalidOperationException){return;}
        core.EditFurnishing(sourceId,action,current.X+dx,current.Y+dy,current.Yaw+yaw,action=="snap");
    }
    public void Refresh()
    {
        if(selected.Length==0||!Visible&&cacheKey.Length>0)return;
        if(core.Instance==null||core.Character?.Connected!=true){selected="";core.SelectedPlacementId=null;Hide();return;}
        var key=core.Instance.Id+":"+core.Instance.Revision+":"+selected;if(key!=cacheKey){cacheKey=key;ReadPlacement();}
        var row=core.Connection?.Db.OwnInteractions.Iter().FirstOrDefault(r=>r.PlacementId==selected);
        var fitting=core.Connection?.Db.OwnAuthoredFlightPowerFittings.Iter().FirstOrDefault(f=>f.PlacedObjectId==selected);
        var storage=core.Inventory.Containers.FirstOrDefault(c=>c.PlacementId==selected);
        var damage=core.Connection?.Db.OwnShipComponentDamage.Iter().FirstOrDefault(d=>d.ShipId==core.ActiveShipId&&(d.ObjectId==selected||d.ObjectId==sourceId));
        var piece=placement.ValueKind==JsonValueKind.Undefined?null:placement.GetProperty("piece").GetString();
        name.Text=row?.Name??storage?.Name??(piece!=null?CultureInfo.InvariantCulture.TextInfo.ToTitleCase(piece.Replace('.',' ').Replace('-',' ')):"Object");
        description.Text=row?.Kind??fitting?.Kind??(placement.ValueKind!=JsonValueKind.Undefined?placement.GetProperty("role").GetString():"No object metadata is currently disclosed.");
        var lines=new System.Collections.Generic.List<string>();
        if(row!=null){lines.Add(row.Reachable?"Within reach":"Move within reach");lines.Add(row.Occupied?(row.SeatedByYou?"Occupied by you":"Occupied"):"Available");if(core.Character is {} actor)lines.Add($"Distance {Math.Sqrt(Math.Pow(row.LocalX-actor.LocalX,2)+Math.Pow(row.LocalY-actor.LocalY,2)):F1} m");}
        if(storage!=null)lines.Add($"Storage {storage.Width} × {storage.Height} cells · {storage.MaxMassKg:F1} kg limit");
        if(damage!=null)lines.Add($"Health {damage.Hp:F0} / {damage.MaxHp:F0}\n{damage.State} · Performance {damage.Performance:P0}");
        if(fitting!=null){lines.Add("Power "+(fitting.Powered?"connected":"disconnected"));if(!core.DevicePowerAvailable)lines.Add(core.DevicePowerUnavailableReason);}
        if(placement.ValueKind!=JsonValueKind.Undefined){var matrix=placement.GetProperty("matrix");lines.Add($"Deck position {matrix[12].GetDouble():F2}, {-matrix[14].GetDouble():F2} m");}
        stats.Text=string.Join("\n",lines);
        var action=core.ResolveSelectedInteraction(selected);interact.Visible=action!=null;interact.Text=action?.Label??"Use";interact.Disabled=action?.Enabled!=true||core.GameplayPending;
        power.Visible=fitting?.Kind is "computer" or "actuator";power.Text=fitting?.Powered==true?"Disconnect power":"Connect power";power.Disabled=!core.DevicePowerAvailable||core.Ship?.Id!=core.Instance.Id||core.Flight?.Active!=true||core.GameplayPending;power.TooltipText=core.DevicePowerAvailable?"":core.DevicePowerUnavailableReason;
        furnishing.Visible=movable&&core.Ship?.Id==core.Instance.Id;foreach(var button in Buttons(furnishing))button.Disabled=core.GameplayPending;
        result.Text=core.GameplayMessage;result.Visible=result.Text.Length>0;
    }
    private static IEnumerable<Button> Buttons(Node parent)
    {foreach(var child in parent.GetChildren()){if(child is Button button)yield return button;foreach(var nested in Buttons(child))yield return nested;}}
}
