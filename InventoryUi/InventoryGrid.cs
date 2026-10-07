using System;
using System.Linq;
using Godot;
using Sidereal.Ui;
using Sidereal.Native;

namespace Sidereal.InventoryUi;

internal static class ItemDrag
{
    public static InventoryInteractionView? Owner;
    public static void Released(Control owner)=>Owner?.CancelLocal();
    public static bool TooltipsAllowed(Control owner,ClientCore core)=>Owner?.Core==core?Owner.TooltipsAllowed():!core.InventoryPending;
    public static bool Ghost(string item)=>Owner?.OriginGhost(item)==true;
    public static bool InteractionEnabled(ClientCore core)=>Owner?.Core!=core||Owner.Enabled?.Invoke()!=false;
    public static string Tooltip(InventoryItemView item)=>item.Definition is {} d
        ? $"{d.Name}\n{d.Role}\n{d.Width} × {d.Height} cells · {d.MassKg:0.##} kg dry mass\nDefinition revision {d.Revision}"+(d.EquipSlot.Length>0?$"\nEquipment slot: {d.EquipSlot}":"")
        : $"{item.DefinitionId}\nThis pinned definition is unavailable. Update the client or reconnect. Packing is disabled until its footprint is known.";
}

public partial class InventoryGrid : Control, IInventoryInteractionSurface
{
    ClientCore IInventoryInteractionSurface.InventoryCore=>core;
    bool IInventoryInteractionSurface.InventoryDemo=>demo;
    private readonly ClientCore core;
    private readonly bool demo;
    private InventorySnapshot snapshot;
    private InventoryContainerView container;
    private string hovered = "";
    public string SelectedId { get; set; } = "";
    public bool ListMode { get; set; }
    public int Sort { get; set; }
    public Func<InventoryItemView,bool>? Filter { get; set; }
    public event Action<string>? SelectionChanged;
    public event Action<string,Vector2>? ContextRequested;
    private InventoryItemView[] VisibleItems => snapshot.Items.Where(i=>i.ContainerId==container.Id&&(Filter?.Invoke(i)??true))
        .OrderBy(i=>Sort==1?RarityOrder(ItemPresentation.Rarity(i.Definition)):0).ThenBy(i=>Sort==2?ItemPresentation.Category(i,snapshot):"").ThenBy(i=>i.Name).ToArray();
    private static int RarityOrder(string rarity)=>rarity switch {"legendary"=>0,"epic"=>1,"rare"=>2,"uncommon"=>3,_=>4};
    private const float Cell = 48;

    public InventoryGrid(ClientCore core, bool demo, InventorySnapshot snapshot, InventoryContainerView container)
    {
        this.core = core; this.demo = demo; this.snapshot = snapshot; this.container = container;
        Name="InventoryGrid";MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        FocusMode = FocusModeEnum.All;
        CustomMinimumSize = new Vector2(container.Width * Cell, container.Height * Cell);
        MouseExited += () => { hovered = ""; TooltipText = ""; QueueRedraw(); };
        Resized += UpdateHitBounds;
    }

    public void Refresh(InventorySnapshot next)
    {
        snapshot = next;
        container = next.Container(container.Id) ?? container;
        UpdateHitBounds();
        QueueRedraw();
    }

    private void UpdateHitBounds()
    {
        // Logical 48-LU cells match the browser. Narrow frames scroll rather than
        // shrinking footprints or silently changing hit geometry.
        var height = ListMode ? Math.Max(64, VisibleItems.Length * 64) : container.Height * Cell;
        if (!Mathf.IsEqualApprox(CustomMinimumSize.Y, height) || !Mathf.IsEqualApprox(CustomMinimumSize.X, ListMode ? 200 : container.Width * Cell))
            CustomMinimumSize = new Vector2(ListMode ? 200 : container.Width * Cell, height);
        QueueRedraw();
    }

    public object SmokeGeometry()
    {
        var rect = GetGlobalRect();
        return new { container = container.Id, columns = container.Width, rows = container.Height,
            x = rect.Position.X, y = rect.Position.Y, width = rect.Size.X, height = rect.Size.Y,
            pitch = Cell * GetGlobalTransform().Scale.X };
    }

    public override void _Process(double delta)
    {
        var mouse = GetLocalMousePosition();
        var item = Hit(mouse);
        if (hovered != (item?.Id ?? "")) { hovered = item?.Id ?? ""; QueueRedraw(); }
        TooltipText=ItemDrag.TooltipsAllowed(this,core)?item==null?"Click or drag an item here. R rotates a held item.":ItemPresentation.Tooltip(core,item):"";
    }

    private Vector2I CellAt(Vector2 position) => new((int)Math.Floor(position.X / Cell), (int)Math.Floor(position.Y / Cell));
    private Vector2I LandingCell(Vector2 position,InventoryItemView item,bool rotated,Vector2 fraction)
    {
        var (w,h)=item.Definition!.Footprint(rotated);
        var corner=position-fraction*new Vector2(w*Cell-4,h*Cell-4)-Vector2.One*2;
        return new Vector2I(Math.Clamp((int)Math.Floor(corner.X/Cell+.5),0,Math.Max(0,container.Width-w)),
            Math.Clamp((int)Math.Floor(corner.Y/Cell+.5),0,Math.Max(0,container.Height-h)));
    }
    private InventoryItemView? Hit(Vector2 position)
    {
        if(ListMode) {var index=(int)Math.Floor(position.Y/64);return index>=0&&index<VisibleItems.Length?VisibleItems[index]:null;}
        var cell = CellAt(position);
        return snapshot.Items.FirstOrDefault(i => {
            if (i.ContainerId != container.Id || !(Filter?.Invoke(i) ?? true)) return false;
            var (w, h) = i.Definition?.Footprint(i.Rotated) ?? (1, 1);
            return cell.X >= i.X && cell.X < i.X + w && cell.Y >= i.Y && cell.Y < i.Y + h;
        });
    }
    public override GodotObject _MakeCustomTooltip(string forText)
    {
        if(forText.Length==0||!ItemDrag.TooltipsAllowed(this,core))return null!;
        var item = snapshot.Item(hovered);
        if(item==null)return UiKit.Tooltip("Inventory grid",forText);
        var (w,h)=item.Definition?.Footprint(item.Rotated)??(1,1);
        var anchor=ListMode?new Rect2(0,Array.FindIndex(VisibleItems,i=>i.Id==item.Id)*64,Size.X,64):new Rect2(item.X*Cell,item.Y*Cell,w*Cell,h*Cell);
        return new ItemTooltip(this,core,item,"Click to pick up · Drag to move · R rotates · Right-click for actions",anchor);
    }

    private Rect2 ItemRect(InventoryItemView item)
    {
        if(ListMode)return new Rect2(0,Array.FindIndex(VisibleItems,i=>i.Id==item.Id)*64,Size.X,60);
        var (w,h)=item.Definition?.Footprint(item.Rotated)??(1,1);return new Rect2(item.X*Cell+2,item.Y*Cell+2,w*Cell-4,h*Cell-4);
    }
    InventorySourceHit? IInventoryInteractionSurface.InventorySource(Vector2 local)
    {
        var item=Hit(local);return item==null||!ItemRect(item).HasPoint(local)?null:new(this,item,ItemRect(item),()=>{SelectedId=item.Id;SelectionChanged?.Invoke(item.Id);QueueRedraw();});
    }
    InventorySourceHit? IInventoryInteractionSurface.InventoryOrigin(string id)=>VisibleItems.FirstOrDefault(i=>i.Id==id) is {} item?new(this,item,ItemRect(item)):null;
    InventoryTarget? IInventoryInteractionSurface.InventoryTarget(InventoryItemView item,bool rotated,Vector2 fraction,Vector2 local)
    {
        if(!new Rect2(Vector2.Zero,Size).HasPoint(local))return null;
        if(Hit(local) is {} onto&&onto.Id!=item.Id&&snapshot.Containers.FirstOrDefault(c=>c.ParentItemId==onto.Id&&c.Kind=="grid") is {} child)
            return new(this,ItemRect(onto),InventoryTargetKind.Transfer,snapshot.FirstPlacement(item.Id,child.Id)!=null,"No room in this storage.",child.Id,Priority:30);
        if(ListMode)return new(this,new Rect2(Vector2.Zero,Size),InventoryTargetKind.Transfer,snapshot.FirstPlacement(item.Id,container.Id)!=null,"No room in this storage.",container.Id,Priority:20);
        if(item.Definition==null)return new(this,new Rect2(Vector2.Zero,Size),InventoryTargetKind.Blocked,false,"The pinned footprint is unavailable.",Priority:20);
        var cell=LandingCell(local,item,rotated,fraction);var (w,h)=item.Definition.Footprint(rotated);
        var fits=snapshot.Fits(item.Id,container.Id,cell.X,cell.Y,rotated);
        return new(this,new Rect2(cell.X*Cell+2,cell.Y*Cell+2,w*Cell-4,h*Cell-4),InventoryTargetKind.Grid,fits,fits?"":"Space occupied or outside this grid.",container.Id,cell.X,cell.Y,rotated,Priority:20);
    }
    public void PickUpAndRotate(string itemId)
    {var item=snapshot.Item(itemId);if(item?.Definition!=null)ItemDrag.Owner?.PickUpAndRotate(new(this,item,ItemRect(item)));}
    public void QuickTransfer(string itemId)
    {var item=snapshot.Item(itemId);if(item?.Definition!=null)ItemDrag.Owner?.QuickTransfer(new(this,item,ItemRect(item)));}
    public override Variant _GetDragData(Vector2 position)=>default;
    public override void _GuiInput(InputEvent input)
    {
        if(!ItemDrag.InteractionEnabled(core))return;
        if(input is InputEventMouseButton {Pressed:true} mouse&&Hit(mouse.Position) is {} item)
        {
            SelectedId=item.Id;SelectionChanged?.Invoke(item.Id);QueueRedraw();
            if(mouse.ButtonIndex==MouseButton.Right){ContextRequested?.Invoke(item.Id,GetGlobalMousePosition()+GetWindow().Position);AcceptEvent();}
            else if(mouse.ButtonIndex==MouseButton.Left&&mouse.DoubleClick&&item.Definition?.EquipSlot.Length>0){if(demo)DemoInventory.Equip(item.Id);else core.EquipItem(item.Id,snapshot.Revision);AcceptEvent();}
        }
        else if(HasFocus()&&!(input is InputEventKey {Echo:true}))
        {
            var items=VisibleItems;if(items.Length==0)return;var current=Array.FindIndex(items,i=>i.Id==SelectedId);
            var previous=input.IsActionPressed("ui_up")||input.IsActionPressed("ui_left");var next=input.IsActionPressed("ui_down")||input.IsActionPressed("ui_right");
            if(previous||next){var direction=previous?-1:1;var index=current<0?(previous?items.Length-1:0):(current+direction+items.Length)%items.Length;SelectedId=items[index].Id;SelectionChanged?.Invoke(SelectedId);QueueRedraw();AcceptEvent();}
            else if(input.IsActionPressed("ui_accept")&&snapshot.Item(SelectedId)!=null){ContextRequested?.Invoke(SelectedId,GetGlobalRect().GetCenter()+GetWindow().Position);AcceptEvent();}
        }
    }

    public override void _Draw()
    {
        var p = SiderealPalette.Current;
        var pitch = Cell;
        var font = GetThemeFont("font", "Label");
        if(ListMode){DrawList(font);return;}
        for (var y = 0; y < container.Height; y++) for (var x = 0; x < container.Width; x++)
        {
            var rect = new Rect2(x * pitch + 1, y * pitch + 1, pitch - 2, pitch - 2);
            DrawRect(rect, p.Surface with { A = Math.Clamp(p.Opacity + .06f, .1f, .98f) });
            DrawRect(rect, p.Accent with { A = .18f }, false, 1);
        }
        foreach (var item in snapshot.Items.Where(i => i.ContainerId == container.Id))
        {
            var (w, h) = item.Definition?.Footprint(item.Rotated) ?? (1, 1);
            var rect = new Rect2(item.X * pitch + 2, item.Y * pitch + 2, w * pitch - 4, h * pitch - 4);
            var color = p.Rarity(ItemPresentation.Rarity(item.Definition));
            var matches=Filter?.Invoke(item)??true;
            var alpha = !matches ? .13f : ItemDrag.Ghost(item.Id) ? .34f : 1;
            ItemFrameStyle.Paint(this,rect,ItemPresentation.Rarity(item.Definition),item.Id==SelectedId,item.Id==hovered,HasFocus()&&item.Id==SelectedId,core.InventoryPending,false,alpha);
            if(!matches)continue;
            var label = Fit(font, item.Name, rect.Size.X - 12, 13);
            DrawString(font, rect.Position + new Vector2(6, rect.Size.Y - 8), label, HorizontalAlignment.Left, rect.Size.X - 12, 13, p.Text with{A=alpha});
            // Silhouette glyphs are interface art, not a substitute for the item model or its physical bounds.
            var center = rect.GetCenter() + new Vector2(0, -7);
            var span = Math.Min(rect.Size.X, rect.Size.Y) * .24f;
            if (InventoryIcons.Texture(item.Definition) is { } texture)
            {
                var bounds = new Rect2(rect.Position + new Vector2(5, 6), new Vector2(rect.Size.X - 10, Math.Max(1, rect.Size.Y - 26)));
                var turns = InventoryIcons.QuarterTurns(item.Definition!, texture, item.Rotated);
                DrawSetTransform(bounds.GetCenter(), turns * Mathf.Pi / 2);
                var localSize = turns % 2 == 1 ? new Vector2(bounds.Size.Y, bounds.Size.X) : bounds.Size;
                DrawTextureRect(texture, InventoryIcons.Fit(texture, new Rect2(-localSize * .5f, localSize)), false, Colors.White with { A = ItemDrag.Ghost(item.Id) ? .34f : 1 });
                DrawSetTransform(Vector2.Zero);
            }
            else switch (item.Definition?.Category)
            {
                case "medical": DrawLine(center - Vector2.Right * span, center + Vector2.Right * span, color, 6); DrawLine(center - Vector2.Down * span, center + Vector2.Down * span, color, 6); break;
                case "weapon": DrawLine(center - new Vector2(span, -span), center + new Vector2(span, -span), color, 5); DrawLine(center, center + new Vector2(span * .4f, span), color, 5); break;
                case "tool": DrawArc(center, span, .4f, 5.6f, 16, color, 3); DrawLine(center + Vector2.Down * span * .4f, center + Vector2.Down * span * 1.5f, color, 5); break;
                default: DrawRect(new Rect2(center - Vector2.One * span, Vector2.One * span * 2), color, false, 2); break;
            }
            if (item.Definition == null) DrawString(font, center, "?", HorizontalAlignment.Left, -1, 18, p.Warning);
        }

    }

    private void DrawList(Font font)
    {
        var p=SiderealPalette.Current;var items=VisibleItems;
        if(items.Length==0){DrawString(font,new Vector2(8,25),"No matching items",HorizontalAlignment.Left,Size.X-16,14,p.Muted);return;}
        for(var i=0;i<items.Length;i++)
        {
            var item=items[i];var alpha=ItemDrag.Ghost(item.Id) ? .34f : 1;var rect=new Rect2(0,i*64,Size.X,60);ItemFrameStyle.Paint(this,rect,ItemPresentation.Rarity(item.Definition),item.Id==SelectedId,item.Id==hovered,HasFocus()&&item.Id==SelectedId,core.InventoryPending,false,alpha);
            if(InventoryIcons.Texture(item.Definition) is {} texture)DrawTextureRect(texture,InventoryIcons.Fit(texture,new Rect2(8,rect.Position.Y+5,45,48)),false,Colors.White with{A=alpha});
            DrawString(font,new Vector2(63,rect.Position.Y+24),Fit(font,item.Name,Size.X-72,14),HorizontalAlignment.Left,Size.X-72,14,p.Text with{A=alpha});
            DrawString(font,new Vector2(63,rect.Position.Y+43),$"{ItemPresentation.Category(item,snapshot)} · {item.Definition?.MassKg:0.##} kg",HorizontalAlignment.Left,Size.X-72,11,p.Muted with{A=alpha});
        }
    }

    internal static string Fit(Font font, string text, float width, int size)
    {
        if (font.GetStringSize(text, fontSize: size).X <= width) return text;
        while (text.Length > 1 && font.GetStringSize(text + "…", fontSize: size).X > width) text = text[..^1];
        return text + "…";
    }
}
