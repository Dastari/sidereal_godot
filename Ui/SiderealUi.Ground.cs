using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Sidereal.Native;

namespace Sidereal.Ui;

public partial class SiderealUi
{
    private readonly Dictionary<string,Button> groundLabels=new();
    private bool showGroundLabels=true;
    private bool groundPickupAwaiting;
    private void PickUpGroundItem(string id)
    {if(core.TakeGroundItem(id))groundPickupAwaiting=true;else ShowMessage(core.InventoryMessage);}
    public void ToggleGroundLabels(){showGroundLabels=!showGroundLabels;foreach(var label in groundLabels.Values)label.Visible=showGroundLabels;}
    public void SetGroundItemLabels((string Id,string DefinitionId,bool Reachable,Vector2 Position)[] labels)
    {
        if(groundPickupAwaiting&&!core.InventoryPending){groundPickupAwaiting=false;if(!core.InventoryMessage.Contains("confirmed",StringComparison.OrdinalIgnoreCase))ShowMessage(core.InventoryMessage);}
        var visible=WorldVisible&&InteriorView&&core.Eva==null&&!menu.Visible;
        var current=visible?labels.OrderByDescending(l=>l.Reachable).Take(64).ToArray():Array.Empty<(string Id,string DefinitionId,bool Reachable,Vector2 Position)>();
        var keep=current.Select(l=>l.Id).ToHashSet();
        foreach(var id in groundLabels.Keys.Where(id=>!keep.Contains(id)).ToArray()){groundLabels[id].QueueFree();groundLabels.Remove(id);}
        var transform=GetGlobalTransformWithCanvas().AffineInverse();
        foreach(var label in current)
        {
            if(!groundLabels.TryGetValue(label.Id,out var button))
            {
                var id=label.Id;button=UiKit.Button("",()=>PickUpGroundItem(id));button.AddThemeFontSizeOverride("font_size",13);
                button.CustomMinimumSize=new Vector2(110,30);button.ZIndex=-1;AddChild(button);groundLabels[id]=button;
            }
            var name=core.Inventory.Items.FirstOrDefault(i=>i.Id==label.Id)?.Name;
            button.Text=name??label.DefinitionId.Replace('-',' ');button.Disabled=!label.Reachable||core.InventoryPending||!core.Alive||core.Resting||core.IsPiloting;
            button.TooltipText=label.Reachable?"Pick up into carried storage.":"Move within reach to pick up.";
            button.Size=new Vector2(Math.Clamp(button.GetCombinedMinimumSize().X,110,220),30);
            var point=transform*label.Position;button.Position=point-new Vector2(button.Size.X/2,button.Size.Y);
            button.Visible=showGroundLabels&&UsableBounds.Encloses(button.GetRect());
        }
    }
}
