using System;
using System.Linq;
using Godot;
using Sidereal.Native;
using Sidereal.Ui;

namespace Sidereal.InventoryUi;

public partial class EquipmentPanel : VBoxContainer
{
    private readonly ClientCore core;
    private readonly bool demo;
    private readonly Label summary;
    private readonly EquipmentSlot[] slots;
    private readonly CrewPreviewView silhouette;
    public event Action<string>? InspectRequested;
    public object PreviewFacts=>silhouette.SmokeFacts();
    public EquipmentPanel(ClientCore core, bool demo)
    {
        this.core = core; this.demo = demo;
        InventoryPresentation.EnsureCatalogue();
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 12);
        summary = UiKit.Label("Equipment reflects your current server loadout.", 13);
        summary.AutowrapMode = TextServer.AutowrapMode.WordSmart; AddChild(summary);
        var body = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 8); AddChild(body);
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 6); right.AddThemeConstantOverride("separation", 6);
        silhouette = new CrewPreviewView(core,demo) { CustomMinimumSize = new Vector2(150, 344), SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddChild(left); body.AddChild(silhouette); body.AddChild(right);
        slots = InventorySnapshot.EquipmentSlots.Select(slot => new EquipmentSlot(core, demo, slot)).ToArray();
        foreach(var slot in slots)slot.InspectRequested+=id=>InspectRequested?.Invoke(id);
        for (var i = 0; i < slots.Length; i++) (i < 5 ? left : right).AddChild(slots[i]);
        var note = UiKit.Label("Drag equipment onto a matching slot. Drag it back to storage to stow it.\nRight-click a slot to inspect · Double-click to stow · Drag the preview to rotate.", 12);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart; note.SizeFlagsHorizontal = SizeFlags.ExpandFill; AddChild(note);
        Resized+=()=>{silhouette.Visible=Size.X>=400;foreach(var slot in slots)slot.CustomMinimumSize=new Vector2(Size.X<400?100:132,53);};
        Refresh();
    }
    public void Refresh()
    {
        var snapshot = InventoryPresentation.Read(core, demo);
        summary.Text = demo ? "UI demo · local equipment specimens" : snapshot.Available ? $"{snapshot.Items.Count(i => i.EquipmentSlot.Length > 0)} equipped items    ·    Revision {snapshot.Revision}" : "Enter the world to load your equipped items.";
        foreach (var slot in slots) slot.Refresh(snapshot);
    }
}

internal partial class EquipmentSlot : Control
{
    private readonly ClientCore core;
    private readonly bool demo;
    private readonly string slot;
    private InventorySnapshot snapshot = InventorySnapshot.Empty;
    private InventoryItemView? item;
    private bool hover;
    public event Action<string>? InspectRequested;
    public EquipmentSlot(ClientCore core, bool demo, string slot)
    {
        this.core = core; this.demo = demo; this.slot = slot;
        CustomMinimumSize = new Vector2(132, 53); SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode=FocusModeEnum.All;FocusEntered+=QueueRedraw;FocusExited+=QueueRedraw;
        MouseEntered += () => { hover = true; QueueRedraw(); };
        MouseExited += () => { hover = false; QueueRedraw(); };
    }
    public void Refresh(InventorySnapshot next)
    {
        snapshot = next; item = snapshot.Items.FirstOrDefault(i => i.EquipmentSlot == slot);
        TooltipText = ItemDrag.TooltipsAllowed(this,core)?item != null ? ItemPresentation.Tooltip(core,item) : $"{slot}: empty\nDrag an equippable item for this slot here.":"";
        QueueRedraw();
    }
    public override bool _CanDropData(Vector2 position, Variant data) => ItemDrag.Payload(data) && ItemDrag.Current is { } drag && drag.Core == core && drag.Demo == demo && !core.InventoryPending && drag.Item.Definition?.EquipSlot == slot;
    public override GodotObject _MakeCustomTooltip(string forText) => UiKit.Tooltip(item?.Name ?? char.ToUpperInvariant(slot[0]) + slot[1..], item == null ? forText : ItemPresentation.Tooltip(core,item).Split('\n', 2).ElementAtOrDefault(1) ?? "",ItemPresentation.Rarity(item?.Definition));
    public override void _DropData(Vector2 position, Variant data)
    {
        if (!_CanDropData(position, data) || ItemDrag.Current is not { } drag) return;
        if (demo) DemoInventory.Equip(drag.Item.Id); else core.EquipItem(drag.Item.Id, drag.Revision);
    }
    public override Variant _GetDragData(Vector2 position)
    {
        if (item?.Definition == null || core.InventoryPending) return default;
        var drag = new ItemDrag { Core = core, Item = item, Revision = snapshot.Revision, Rotated = item.Rotated, Demo = demo,
            GrabFraction=new Vector2(Mathf.Clamp(position.X/Math.Max(1,Size.X),0,1),Mathf.Clamp(position.Y/Math.Max(1,Size.Y),0,1)) };
        ItemDrag.Current = drag; drag.Preview = new InventoryDragPreview(drag); SetDragPreview(drag.Preview); core.ReleaseControls();
        return new Godot.Collections.Dictionary { ["sidereal-item"] = item.Id };
    }
    public override void _Notification(int what) { if (what == NotificationDragEnd) ItemDrag.Released(this); }
    public override void _GuiInput(InputEvent input)
    {
        if(item==null)return;
        if(input.IsActionPressed("ui_accept")){InspectRequested?.Invoke(item.Id);AcceptEvent();return;}
        if(input is InputEventMouseButton {Pressed:true,ButtonIndex:MouseButton.Right}){InspectRequested?.Invoke(item.Id);AcceptEvent();return;}
        if(input is InputEventMouseButton {Pressed:true,ButtonIndex:MouseButton.Left} mouse)
        {
            if(mouse.DoubleClick)
            {
                var target=snapshot.Containers.FirstOrDefault(c=>c.Carried&&c.Kind=="grid"&&snapshot.FirstPlacement(item.Id,c.Id)!=null);
                if(target!=null){if(demo&&snapshot.FirstPlacement(item.Id,target.Id) is {} placement)DemoInventory.Move(item.Id,target.Id,placement.X,placement.Y,placement.Rotated);else core.TransferItem(item.Id,target.Id,snapshot.Revision);}
            }
            if(mouse.DoubleClick)AcceptEvent();
        }
    }
    public override void _Draw()
    {
        var p = SiderealPalette.Current; var font = GetThemeFont("font", "Label");
        var color = item != null ? p.Rarity(ItemPresentation.Rarity(item.Definition)) : p.Accent;
        ItemFrameStyle.Paint(this,new Rect2(Vector2.Zero,Size),ItemPresentation.Rarity(item?.Definition),item!=null,hover,HasFocus(),core.InventoryPending,item==null);
        DrawString(font, new Vector2(10, 18), char.ToUpperInvariant(slot[0]) + slot[1..], HorizontalAlignment.Left, Size.X - 20, 12, p.Accent);
        var texture = InventoryIcons.Texture(item?.Definition);
        var available = Size.X - (texture != null ? 55 : 20);
        DrawString(font, new Vector2(10, 39), InventoryGrid.Fit(font, item?.Name ?? "Empty", available, 13), HorizontalAlignment.Left, available, 13, item != null ? p.Text : p.Muted);
        if (texture != null) DrawTextureRect(texture, InventoryIcons.Fit(texture, new Rect2(Size.X - 44, 17, 36, 31)), false);
    }
}
