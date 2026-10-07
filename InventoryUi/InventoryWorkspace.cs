using System;
using System.Linq;
using Godot;
using Sidereal.Native;
using Sidereal.Ui;

namespace Sidereal.InventoryUi;

internal static class DemoInventory
{
    public static InventorySnapshot Snapshot { get; private set; } = InventorySnapshot.Demo();
    public static string Message { get; private set; } = "UI demo: specimen items, local interactions, no server changes.";
    public static void Move(string item, string container, int x, int y, bool rotated)
    {
        if (!Snapshot.Fits(item, container, x, y, rotated)) return;
        Snapshot = Snapshot with { Revision = Snapshot.Revision + 1, Items = Snapshot.Items.Select(i => i.Id == item ? i with { ContainerId = container, EquipmentSlot = "", X = x, Y = y, Rotated = rotated } : i).ToArray() };
        Message = "UI demo: layout changed locally. Live moves require server confirmation.";
    }
    public static void Equip(string itemId)
    {
        var item = Snapshot.Item(itemId);
        if (item?.Definition is not { EquipSlot.Length: > 0 } definition) return;
        var old = Snapshot.Items.FirstOrDefault(i => i.EquipmentSlot == definition.EquipSlot && i.Id != itemId);
        if (old != null)
        {
            var stowed = false;
            foreach (var container in Snapshot.Containers.Where(c => c.Kind == "grid" && c.Carried))
            {
                for (var y = 0; y < container.Height && !stowed; y++) for (var x = 0; x < container.Width && !stowed; x++)
                    if (Snapshot.Fits(old.Id, container.Id, x, y, false)) { Move(old.Id, container.Id, x, y, false); stowed = true; }
                if (stowed) break;
            }
            if (!stowed) { Message = "UI demo: no carried space for the previous item."; return; }
        }
        Snapshot = Snapshot with { Revision = Snapshot.Revision + 1, Items = Snapshot.Items.Select(i => i.Id == itemId ? i with { ContainerId = "", EquipmentSlot = definition.EquipSlot, X = 0, Y = 0, Rotated = false } : i).ToArray() };
        Message = "UI demo: equipment preview changed locally.";
    }
    public static void Assign(byte slot, string item)
    {
        var hotbar = Snapshot.Hotbar.ToDictionary(p => p.Key, p => p.Value); hotbar[slot] = item;
        Snapshot = Snapshot with { Revision = Snapshot.Revision + 1, Hotbar = hotbar };
        Message = "UI demo: hotbar reference assigned locally.";
    }
}

internal static class InventoryPresentation
{
    private static bool loaded;
    public static void EnsureCatalogue()
    {
        if (loaded) return;
        var seed = FileAccess.GetFileAsString("res://InventoryUi/item-seed-v1.json");
        InventoryCatalog.LoadRevisionOneSeed(seed); loaded = true;
    }
    public static InventorySnapshot Read(ClientCore core, bool demo) => demo ? DemoInventory.Snapshot : core.Inventory;
}

public partial class InventoryWorkspace : VBoxContainer
{
    private readonly ClientCore core; private readonly bool demo;
    private readonly Label summary, message, reservoir, selectedDetail;
    private readonly Button starter; private readonly ContainerPane left,right;
    private readonly GridContainer columns; private string selected="", detailKey="";
    public bool InteractionActive => ItemDrag.Current?.Core==core;
    public string SelectedItemId=>selected;
    public event Action<string>? ContainerOpenRequested;
    public InventoryWorkspace(ClientCore core,bool demo)
    {
        this.core=core;this.demo=demo;InventoryPresentation.EnsureCatalogue();SizeFlagsHorizontal=SizeFlags.ExpandFill;AddThemeConstantOverride("separation",10);
        summary=UiKit.Label("Connect to load your inventory",16);AddChild(summary);
        message=UiKit.Paragraph("",0);message.AddThemeFontSizeOverride("font_size",13);AddChild(message);
        starter=UiKit.Button("Claim starter equipment",()=>{if(!demo)core.ClaimStarterKit();});AddChild(starter);
        columns=new GridContainer {Columns=2,SizeFlagsHorizontal=SizeFlags.ExpandFill};columns.AddThemeConstantOverride("h_separation",12);columns.AddThemeConstantOverride("v_separation",16);AddChild(columns);
        left=new ContainerPane(core,demo,0);right=new ContainerPane(core,demo,1);columns.AddChild(left);columns.AddChild(right);
        left.Selected+=SelectDetails;right.Selected+=SelectDetails;
        left.ContainerOpenRequested+=id=>ContainerOpenRequested?.Invoke(id);right.ContainerOpenRequested+=id=>ContainerOpenRequested?.Invoke(id);
        selectedDetail=UiKit.Paragraph("Select an item to inspect its pinned definition.",0);selectedDetail.AddThemeFontSizeOverride("font_size",14);AddChild(UiKit.Panel(selectedDetail,8,"QuietPanel"));
        reservoir=UiKit.Paragraph("",0);reservoir.AddThemeFontSizeOverride("font_size",13);AddChild(reservoir);
        AddChild(UiKit.Paragraph("Drag to move · R rotates · Double-click equips · Right-click for actions. Filters keep occupied cells reserved.",0).Sized(12));
        Resized+=()=>columns.Columns=Size.X<620?1:2;Refresh();
    }
    private void SelectDetails(string itemId)
    { selected=itemId; left.Select(itemId);right.Select(itemId);UpdateDetails(InventoryPresentation.Read(core,demo)); }
    public void SelectItem(string itemId)
    {
        selected=itemId;var snapshot=InventoryPresentation.Read(core,demo);
        if(snapshot.Item(itemId) is {} item&&item.ContainerId.Length>0)left.OpenContainer(item.ContainerId);
        left.Select(itemId);right.Select(itemId);UpdateDetails(snapshot);
    }
    public bool OpenContainer(string id)=>right.OpenContainer(id);
    private void UpdateDetails(InventorySnapshot snapshot)
    {
        var pin=core.Connection?.Db.OwnItemDefinitionPins.Iter().FirstOrDefault(p=>p.ItemId==selected);
        var key=selected+":"+snapshot.Revision+":"+snapshot.Item(selected)?.ScopedRevision+":"+snapshot.Item(selected)?.Definition?.Revision+":"+pin?.WeaponRevision;if(key==detailKey)return;detailKey=key;
        var item=snapshot.Item(selected);
        selectedDetail.Text=item==null?"Select an item to inspect its pinned definition.":ItemPresentation.Tooltip(core,item)+$"\n{ItemPresentation.Category(item,snapshot)} · Rarity: {ItemPresentation.Rarity(item.Definition)}"+
            (snapshot.Containers.FirstOrDefault(c=>c.ParentItemId==item.Id) is {} child?$"\nContains {snapshot.Items.Count(i=>i.ContainerId==child.Id)} items":"");
    }
    public void Refresh()
    {
        var snapshot=InventoryPresentation.Read(core,demo);
        summary.Text=snapshot.Available?$"Carried {snapshot.CarriedMassKg:0.##} / {snapshot.CarryLimitKg:0.##} kg":"Enter the world to load your private inventory.";
        message.Text=demo?DemoInventory.Message:core.InventoryMessage;message.Modulate=(!core.InventoryMessage.StartsWith("Rejected")?SiderealPalette.Current.Muted:SiderealPalette.Current.Warning);
        starter.Visible=!demo&&core.Connection!=null&&!snapshot.Available;
        left.Refresh(snapshot);right.Refresh(snapshot);
        reservoir.Text=string.Join("\n",snapshot.Containers.Where(c=>c.Kind=="liquid").Select(c=>$"{c.Name}: {c.AmountLitres:0.##} / {c.CapacityLitres:0.##} L {c.LiquidType}"));reservoir.Visible=reservoir.Text.Length>0;
        if(snapshot.Item(selected)==null)selected="";UpdateDetails(snapshot);
    }
}

internal partial class ContainerPane : VBoxContainer
{
    private readonly ClientCore core;private readonly bool demo;private readonly int initialIndex;private readonly string fixedContainerId;
    private readonly OptionButton choice,category,sort;private readonly LineEdit search;private readonly Label capacity;private readonly CheckButton list;
    private readonly ScrollContainer gridScroll;private readonly PopupMenu context;private readonly Button bulk,rarity;
    private string rarityFilter="All";
    private InventoryGrid? grid;private InventorySnapshot snapshot=InventorySnapshot.Empty;
    private string[] ids=Array.Empty<string>();private string chosen="",contextItem="",selected="";
    public event Action<string>? Selected;
    public event Action<string>? ContainerOpenRequested;
    public ContainerPane(ClientCore core,bool demo,int initialIndex,string fixedContainerId="")
    {
        this.core=core;this.demo=demo;this.initialIndex=initialIndex;this.fixedContainerId=fixedContainerId;SizeFlagsHorizontal=SizeFlags.ExpandFill;CustomMinimumSize=new Vector2(230,0);AddThemeConstantOverride("separation",7);
        choice=new OptionButton {ThemeTypeVariation="CompactOption",SizeFlagsHorizontal=SizeFlags.ExpandFill,CustomMinimumSize=new Vector2(0,32),TooltipText="Only storage currently disclosed by the server is listed."};choice.AddThemeFontSizeOverride("font_size",14);AddChild(choice);
        choice.ItemSelected+=index=>{chosen=ids[(int)index];ReplaceGrid();};
        choice.Visible=fixedContainerId.Length==0;
        var filter=new HBoxContainer();category=new OptionButton {ThemeTypeVariation="CompactOption",SizeFlagsHorizontal=SizeFlags.ExpandFill};category.AddThemeFontSizeOverride("font_size",13);foreach(var name in new[]{"All","Weapons","Armor","Tools","Supplies","Storage"})category.AddItem(name);filter.AddChild(category);
        rarity=new Button {Text="Any rarity",CustomMinimumSize=new Vector2(76,30),TooltipText="Filter by item rarity."};rarity.AddThemeFontSizeOverride("font_size",11);filter.AddChild(rarity);
        rarity.Pressed+=()=>{var grades=new[]{"All","common","uncommon","rare","epic","legendary"};rarityFilter=grades[(Array.IndexOf(grades,rarityFilter)+1)%grades.Length];rarity.Text=rarityFilter=="All"?"Any rarity":char.ToUpperInvariant(rarityFilter[0])+rarityFilter[1..];UpdateFilter();};
        list=new CheckButton {Text="List",TooltipText="Show a sorted item list. Grid placement retains its logical cells."};list.AddThemeFontSizeOverride("font_size",13);filter.AddChild(list);AddChild(filter);
        var query=new HBoxContainer();query.AddThemeConstantOverride("separation",6);AddChild(query);
        search=new LineEdit {ThemeTypeVariation="CompactInput",PlaceholderText="Search items…",ClearButtonEnabled=true,SizeFlagsHorizontal=SizeFlags.ExpandFill};search.AddThemeFontSizeOverride("font_size",13);query.AddChild(search);
        sort=new OptionButton {ThemeTypeVariation="CompactOption",CustomMinimumSize=new Vector2(84,30),Disabled=true};sort.AddThemeFontSizeOverride("font_size",11);foreach(var name in new[]{"Name","Rarity","Type"})sort.AddItem(name);sort.TooltipText="Sort items in list view.";query.AddChild(sort);
        category.ItemSelected+=_=>UpdateFilter();search.TextChanged+=_=>UpdateFilter();list.Toggled+=_=>UpdateFilter();sort.ItemSelected+=_=>UpdateFilter();
        capacity=UiKit.Paragraph("",0).Sized(12);AddChild(capacity);
        gridScroll=new ScrollContainer {SizeFlagsHorizontal=SizeFlags.ExpandFill,HorizontalScrollMode=ScrollContainer.ScrollMode.Auto,VerticalScrollMode=ScrollContainer.ScrollMode.Auto,CustomMinimumSize=new Vector2(0,200)};AddChild(gridScroll);
        bulk=UiKit.Button("Take all",()=>{if(demo)return;var container=snapshot.Container(chosen);if(container==null)return;if(!container.Carried)core.TakeAll(chosen,snapshot.Revision);else if(BulkDestination() is {} destination)core.StoreAll(chosen,destination,snapshot.Revision);});AddChild(bulk);
        context=new PopupMenu();AddChild(context);context.IdPressed+=ContextAction;
    }
    public void Select(string id){selected=id;if(grid!=null)grid.SelectedId=id;}
    public bool OpenContainer(string id)
    {
        var index=Array.IndexOf(ids,id);if(index<0)return false;chosen=id;choice.Select(index);ReplaceGrid();return true;
    }
    private bool Matches(InventoryItemView item)=>(category.Selected==0||ItemPresentation.Category(item,snapshot)==category.GetItemText(category.Selected))&&
        (rarityFilter=="All"||ItemPresentation.Rarity(item.Definition)==rarityFilter)&&
        (search.Text.Length==0||item.Name.Contains(search.Text,StringComparison.OrdinalIgnoreCase)||item.DefinitionId.Contains(search.Text,StringComparison.OrdinalIgnoreCase));
    private void UpdateFilter(){sort.Disabled=!list.ButtonPressed;if(grid==null)return;grid.Filter=Matches;grid.ListMode=list.ButtonPressed;grid.Sort=sort.Selected;grid.Refresh(snapshot);}
    public void Refresh(InventorySnapshot next)
    {
        snapshot=next;if(contextItem.Length>0&&next.Item(contextItem)==null){contextItem="";context.Hide();}
        var containers=next.Containers.Where(c=>c.Kind=="grid"&&(fixedContainerId.Length==0||c.Id==fixedContainerId)).OrderByDescending(c=>c.Id==next.PocketsId).ThenByDescending(c=>c.Carried).ThenBy(c=>c.Name).ToArray();var nextIds=containers.Select(c=>c.Id).ToArray();
        if(!nextIds.SequenceEqual(ids))
        {
            ids=nextIds;choice.Clear();foreach(var c in containers)choice.AddItem(c.Name+(c.Carried?" · carried":" · nearby"));
            if(!ids.Contains(chosen)){chosen=ids.ElementAtOrDefault(Math.Min(initialIndex,Math.Max(0,ids.Length-1)))??"";context.Hide();if(ItemDrag.Current?.Core==core){GetViewport().GuiCancelDrag();ItemDrag.Current=null;}}
            if(ids.Length>0)choice.Select(Array.IndexOf(ids,chosen));ReplaceGrid();
        }
        var container=next.Container(chosen);capacity.Text=container==null?"No accessible storage.":$"{container.Width} × {container.Height} cells · Payload limit {container.MaxMassKg:0.##} kg";
        bulk.Text=container?.Carried==true?"Store all in nearby storage":"Take all";bulk.Visible=!demo&&container!=null;
        bulk.Disabled=core.InventoryPending||container?.Carried==true&&BulkDestination()==null;
        grid?.Refresh(next);
    }
    private void ReplaceGrid()
    {
        if(grid!=null){gridScroll.RemoveChild(grid);grid.QueueFree();grid=null;}
        if(snapshot.Container(chosen) is not {} container)return;
        grid=new InventoryGrid(core,demo,snapshot,container);grid.SelectedId=selected;grid.SelectionChanged+=id=>{selected=id;Selected?.Invoke(id);};grid.ContextRequested+=OpenContext;
        gridScroll.AddChild(grid);UpdateFilter();
    }
    private string? BulkDestination()=>snapshot.Containers.FirstOrDefault(c=>c.Kind=="grid"&&!c.Carried&&c.Id!=chosen)?.Id;
    private string? Destination(string itemId)
    {
        var item=snapshot.Item(itemId);var source=snapshot.Container(item?.ContainerId??chosen);
        return snapshot.Containers.Where(c=>c.Kind=="grid"&&c.Id!=(item?.ContainerId??chosen)&&c.ParentItemId!=itemId)
            .OrderByDescending(c=>source?.Carried==false&&c.Carried).ThenByDescending(c=>source?.Carried==true&&!c.Carried).ThenByDescending(c=>c.Id==snapshot.PocketsId)
            .FirstOrDefault(c=>item==null||snapshot.FirstPlacement(itemId,c.Id)!=null)?.Id;
    }
    private void OpenContext(string itemId,Vector2 position)
    {
        var item=snapshot.Item(itemId);if(item==null)return;contextItem=itemId;Selected?.Invoke(itemId);context.Clear();context.ContentScaleFactor=GetGlobalTransformWithCanvas().Scale.X;
        context.AddItem("Inspect",0);
        if(item.Definition?.EquipSlot.Length>0)context.AddItem(item.EquipmentSlot.Length>0?"Unequip to carried storage":"Equip",1);
        context.AddItem("Quick transfer",2);
        if(snapshot.Containers.Any(c=>c.ParentItemId==itemId&&c.Kind=="grid"))context.AddItem("Open storage",3);
        context.AddItem("Pick up and rotate",4);
        if(!demo)context.AddItem("Drop on ground",5);
        for(var i=1;i<context.ItemCount;i++)context.SetItemDisabled(i,core.InventoryPending);
        context.Popup(new Rect2I(new Vector2I((int)position.X,(int)position.Y),Vector2I.Zero));
    }
    private void ContextAction(long action)
    {
        var item=snapshot.Item(contextItem);if(item==null)return;
        switch(action)
        {
            case 0:Selected?.Invoke(item.Id);break;
            case 1:if(item.EquipmentSlot.Length>0){var carried=snapshot.Containers.FirstOrDefault(c=>c.Carried&&c.Kind=="grid"&&snapshot.FirstPlacement(item.Id,c.Id)!=null);if(carried!=null)Transfer(item,carried.Id);}else if(demo)DemoInventory.Equip(item.Id);else core.EquipItem(item.Id,snapshot.Revision);break;
            case 2:if(Destination(item.Id) is {} destination)Transfer(item,destination);break;
            case 3:if(snapshot.Containers.FirstOrDefault(c=>c.ParentItemId==item.Id&&c.Kind=="grid") is {} child){if(ContainerOpenRequested!=null)ContainerOpenRequested(child.Id);else OpenContainer(child.Id);}break;
            case 4:grid?.PickUpAndRotate(item.Id);break;
            case 5:core.DropItem(item.Id,snapshot.Revision);break;
        }
    }
    private void Transfer(InventoryItemView item,string destination)
    {if(demo){if(snapshot.FirstPlacement(item.Id,destination) is {} slot)DemoInventory.Move(item.Id,destination,slot.X,slot.Y,slot.Rotated);}else core.TransferItem(item.Id,destination,snapshot.Revision);}
}
