using System;
using System.Collections.Generic;
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
    private readonly CrewPreviewView preview;
    private readonly EquipmentPaperDoll body;
    private readonly Label rotateHint;
    private readonly Button turnLeft, turnRight;
    private EquipmentPaperDollMetrics? layout;
    private static readonly string[] DisplayOrder = { "helmet", "shoulders", "hand", "gloves", "legs", "uniform", "visor", "chest", "back", "belt", "boots" };
    public event Action<string>? InspectRequested;
    // Existing review clients address the domain registry; display order is independent.
    public IReadOnlyList<Control> VisualSlots => slots;
    public bool PreviewInteractionActive => preview.InteractionActive;
    public void CancelPreviewInteraction() => preview.CancelInteraction();
    public object PreviewFacts => preview.SmokeFacts();
    public object SlotFacts => slots.Select(slot => {
        var index = Array.IndexOf(DisplayOrder, slot.SlotId);
        var rect = slot.GetGlobalRect();
        return new { id = slot.SlotId, label = slot.SlotLabel, column = index < 6 ? "left" : "right", row = index % 6,
            x = rect.Position.X, y = rect.Position.Y, width = rect.Size.X, height = rect.Size.Y, visible = slot.IsVisibleInTree() };
    }).ToArray();
    public object LayoutFacts => new { metrics = layout, bodyWidth = body.Size.X, bodyHeight = body.Size.Y,
        leftButton = ControlRect(turnLeft), rightButton = ControlRect(turnRight) };
    private static object ControlRect(Control control) { var rect = control.GetGlobalRect(); return new { x = rect.Position.X, y = rect.Position.Y, width = rect.Size.X, height = rect.Size.Y }; }
    public EquipmentPanel(ClientCore core, bool demo)
    {
        this.core = core; this.demo = demo;
        InventoryPresentation.EnsureCatalogue();
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 12);
        body = new EquipmentPaperDoll { Name = "EquipmentPaperDoll", CustomMinimumSize = new Vector2(0, 490), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(body);
        preview = new CrewPreviewView(core, demo) { Name = "EquipmentPreview", CustomMinimumSize = Vector2.Zero };
        // Wells follow the preview in tree/draw order and win its four-LU overlap.
        body.AddChild(preview);
        slots = InventorySnapshot.EquipmentSlots.Select(slot => new EquipmentSlot(core, demo, slot)).ToArray();
        foreach (var slot in slots) { slot.InspectRequested += id => InspectRequested?.Invoke(id); body.AddChild(slot); }
        rotateHint = UiKit.Label("Drag to rotate", 10); rotateHint.ThemeTypeVariation = "AccentLabel"; rotateHint.MouseFilter = MouseFilterEnum.Ignore; body.AddChild(rotateHint);
        turnLeft = TurnButton("‹", -Mathf.Pi / 6, "Turn character left");
        turnRight = TurnButton("›", Mathf.Pi / 6, "Turn character right");
        body.AddChild(turnLeft); body.AddChild(turnRight);
        summary = UiKit.Label("Equipment reflects your current server loadout.", 13);
        summary.AutowrapMode = TextServer.AutowrapMode.WordSmart; AddChild(summary);
        var note = UiKit.Label("Drag equipment onto a matching slot. Drag it back to storage to stow it.\nRight-click a slot to inspect · Double-click to stow · Drag the preview to rotate.", 12);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart; note.SizeFlagsHorizontal = SizeFlags.ExpandFill; AddChild(note);
        body.Resized += LayoutPaperDoll;
        VisibilityChanged += () => { if (!IsVisibleInTree()) CancelPreviewInteraction(); };
        Refresh();
    }
    private Button TurnButton(string text, float radians, string hint)
    {
        var button = UiKit.Button(text, () => preview.RotatePreview(radians));
        button.Name = radians < 0 ? "CharacterTurnLeft" : "CharacterTurnRight";
        button.TooltipText = hint; button.CustomMinimumSize = new Vector2(28, 27);
        button.AddThemeFontSizeOverride("font_size", 14);
        return button;
    }
    public override void _Ready() => RefreshTurnButtonStyles();
    public override void _Notification(int what)
    {
        if (what == NotificationThemeChanged && IsNodeReady()) RefreshTurnButtonStyles();
    }
    private void RefreshTurnButtonStyles()
    {
        // The regular button has 12-LU content margins. Compact source arrows
        // retain every themed state, with margins that fit their 28x27 wells.
        foreach (var state in new[] { "normal", "hover", "pressed", "disabled", "focus" })
        {
            var source = GetThemeStylebox(state, "Button");
            foreach (var button in new[] { turnLeft, turnRight })
            {
                var padding = state == "focus" ? 0 : 5;
                StyleBox style = source is SciFiFrameStyle frame
                    ? new SciFiFrameStyle(frame.Fill, frame.Border, frame.Accent, padding, frame.Corner, frame.Rails) { BorderWidth = frame.BorderWidth }
                    : (StyleBox)source.Duplicate();
                style.ContentMarginLeft = style.ContentMarginRight = padding;
                style.ContentMarginTop = style.ContentMarginBottom = padding;
                button.AddThemeStyleboxOverride(state, style);
            }
        }
        layout = null;
        LayoutPaperDoll();
    }
    public override void _Process(double delta) { if (IsVisibleInTree()) LayoutPaperDoll(); }
    private void LayoutPaperDoll()
    {
        if (body.Size.X <= 0) return;
        var viewportHeight = 546d;
        for (Node? parent = GetParent(); parent != null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll) { viewportHeight = scroll.Size.Y; break; }
        var next = EquipmentPaperDollMetrics.Create(body.Size.X, viewportHeight);
        if (layout == next) return;
        layout = next;
        preview.Position = new Vector2((float)next.PreviewX, 36);
        preview.Size = new Vector2((float)next.PreviewWidth, (float)next.PreviewHeight);
        for (var i = 0; i < DisplayOrder.Length; i++)
        {
            var slot = slots.First(s => s.SlotId == DisplayOrder[i]);
            slot.Position = new Vector2(i < 6 ? 8 : body.Size.X - (float)next.CardSide - 8, 36 + i % 6 * 65);
            slot.Size = new Vector2((float)next.CardSide, (float)next.CardSide);
        }
        rotateHint.Position = new Vector2((float)next.CardSide + 3, (float)next.PreviewHeight + 45);
        rotateHint.Size = new Vector2((float)next.PreviewWidth, 16);
        turnLeft.Position = new Vector2((float)next.ControlsX, (float)next.ControlsY);
        turnRight.Position = new Vector2((float)(next.ControlsX + next.ControlsWidth - 28), (float)next.ControlsY);
        turnLeft.Size = turnRight.Size = new Vector2(28, 27);
        body.QueueRedraw();
    }
    public void Refresh()
    {
        var snapshot = InventoryPresentation.Read(core, demo);
        summary.Text = demo ? "UI demo · local equipment specimens" : snapshot.Available ? $"{snapshot.Items.Count(i => i.EquipmentSlot.Length > 0)} equipped items    ·    Revision {snapshot.Revision}" : "Enter the world to load your equipped items.";
        foreach (var slot in slots) slot.Refresh(snapshot);
    }
}

/// <summary>Source a356 character-sheet geometry in logical UI units; scroll clipping belongs to the window.</summary>
public readonly record struct EquipmentPaperDollMetrics(double CardSide, double PreviewX, double PreviewWidth, double PreviewHeight,
    double ControlsX, double ControlsY, double ControlsWidth)
{
    public static EquipmentPaperDollMetrics Create(double contentWidth, double viewportHeight)
    {
        var width = Math.Max(1, double.IsFinite(contentWidth) ? contentWidth : 1);
        var height = double.IsFinite(viewportHeight) ? viewportHeight : 546;
        var side = Math.Min(59, Math.Max(40, Math.Floor(width * .245)));
        var previewWidth = Math.Max(1, width - 2 * side - 8);
        var previewHeight = Math.Min(383, Math.Max(96, height - 130));
        var compact = previewHeight < 383;
        return new(side, side + 4, previewWidth, previewHeight, compact ? side + 12 : 12,
            compact ? 36 + previewHeight + 26 : 450, compact ? Math.Max(1, previewWidth - 16) : Math.Max(1, width - 24));
    }
}

internal partial class EquipmentPaperDoll : Control
{
    public EquipmentPaperDoll() { MouseFilter = MouseFilterEnum.Ignore; }
    public override void _Draw()
    {
        var palette = SiderealPalette.Current;
        DrawString(GetThemeFont("font", "Label"), new Vector2(10, 23), "EQUIPMENT", HorizontalAlignment.Left, Math.Max(1, Size.X - 20), 16, palette.Accent);
        DrawLine(new Vector2(10, 29), new Vector2(Math.Max(10, Size.X - 10), 29), palette.Accent with { A = .35f }, 1);
    }
}

internal partial class EquipmentSlot : Control, IInventoryInteractionSurface
{
    ClientCore IInventoryInteractionSurface.InventoryCore=>core;
    bool IInventoryInteractionSurface.InventoryDemo=>demo;
    private readonly ClientCore core;
    private readonly bool demo;
    private readonly string slot;
    private InventorySnapshot snapshot = InventorySnapshot.Empty;
    private InventoryItemView? item;
    private bool hover;
    public string SlotId => slot;
    public string SlotLabel => slot switch { "hand" => "Primary weapon", "chest" => "Chest armor", "back" => "Backpack", _ => char.ToUpperInvariant(slot[0]) + slot[1..] };
    private string EmptyRarity => slot switch { "helmet" => "epic", "hand" => "legendary", "chest" or "uniform" or "boots" => "uncommon", "belt" => "common", _ => "rare" };
    public event Action<string>? InspectRequested;
    public EquipmentSlot(ClientCore core, bool demo, string slot)
    {
        this.core = core; this.demo = demo; this.slot = slot;
        Name = "EquipmentSlot_" + slot; CustomMinimumSize = Vector2.Zero;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode=FocusModeEnum.All;FocusEntered+=QueueRedraw;FocusExited+=QueueRedraw;
        MouseEntered += () => { hover = true; QueueRedraw(); };
        MouseExited += () => { hover = false; QueueRedraw(); };
    }
    public void Refresh(InventorySnapshot next)
    {
        snapshot = next; item = snapshot.Items.FirstOrDefault(i => i.EquipmentSlot == slot);
        TooltipText = ItemDrag.TooltipsAllowed(this,core)?item != null ? ItemPresentation.Tooltip(core,item) : $"{SlotLabel}: empty\nDrag an equippable item for this slot here.":"";
        QueueRedraw();
    }
    public override GodotObject _MakeCustomTooltip(string forText) => forText.Length==0||!ItemDrag.TooltipsAllowed(this,core)?null!:item==null?UiKit.Tooltip(SlotLabel,forText):new ItemTooltip(this,core,item,"Click to pick up · Double-click to stow · Right-click to inspect");
    InventorySourceHit? IInventoryInteractionSurface.InventorySource(Vector2 local)=>item!=null&&new Rect2(Vector2.Zero,Size).HasPoint(local)?new(this,item,new Rect2(Vector2.Zero,Size)):null;
    InventorySourceHit? IInventoryInteractionSurface.InventoryOrigin(string id)=>item?.Id==id?new(this,item,new Rect2(Vector2.Zero,Size)):null;
    InventoryTarget? IInventoryInteractionSurface.InventoryTarget(InventoryItemView source,bool rotated,Vector2 fraction,Vector2 local)
    {
        var rect=new Rect2(Vector2.Zero,Size);if(!rect.HasPoint(local))return null;
        if(slot=="back"&&source.Definition?.EquipSlot!="back")
        {
            var pack=snapshot.Items.FirstOrDefault(i=>i.EquipmentSlot=="back");var bag=snapshot.Containers.FirstOrDefault(c=>c.ParentItemId==pack?.Id&&c.Kind=="grid");
            return new(this,rect,InventoryTargetKind.Transfer,bag!=null&&snapshot.FirstPlacement(source.Id,bag.Id)!=null,bag==null?"Equip a backpack first.":"No room in this backpack.",bag?.Id??"",Priority:40);
        }
        var matches=source.Definition?.EquipSlot==slot;
        return new(this,rect,InventoryTargetKind.Equip,matches,matches?"":"This item does not fit this equipment slot.",Priority:40);
    }
    public override Variant _GetDragData(Vector2 position)=>default;
    public override void _GuiInput(InputEvent input)
    {
        if(!ItemDrag.InteractionEnabled(core))return;
        if(item==null)return;
        if(input.IsActionPressed("ui_accept")){InspectRequested?.Invoke(item.Id);AcceptEvent();return;}
        if(input is InputEventMouseButton {Pressed:true,ButtonIndex:MouseButton.Right}){InspectRequested?.Invoke(item.Id);AcceptEvent();return;}
        if(input is InputEventMouseButton {Pressed:true,ButtonIndex:MouseButton.Left} mouse)
        {
            if(mouse.DoubleClick)
            {
                if(!demo)core.TransferItem(item.Id,"",snapshot.Revision);
                else if(InventoryInteractionView.DefaultCarriedDestination(snapshot,item) is {} destination&&snapshot.FirstPlacement(item.Id,destination) is {} placement)DemoInventory.Move(item.Id,destination,placement.X,placement.Y,placement.Rotated);
            }
            if(mouse.DoubleClick)AcceptEvent();
        }
    }
    public override void _Draw()
    {
        var p = SiderealPalette.Current; var font = GetThemeFont("font", "Label");
        ItemFrameStyle.Paint(this,new Rect2(Vector2.Zero,Size),item != null ? ItemPresentation.Rarity(item.Definition) : EmptyRarity,false,hover,HasFocus(),core.InventoryPending,item==null,item!=null&&ItemDrag.Ghost(item.Id) ? .34f : 1);
        var texture = InventoryIcons.Texture(item?.Definition);
        if (texture != null) DrawTextureRect(texture, InventoryIcons.Fit(texture, new Rect2(7, 7, Math.Max(1, Size.X - 14), Math.Max(1, Size.Y - 14))), false, Colors.White with {A=ItemDrag.Ghost(item?.Id??"") ? .34f : 1});
        else DrawString(font, new Vector2(7, Size.Y - 9), InventoryGrid.Fit(font, item?.Definition == null && item != null ? "?" : SlotLabel, Math.Max(1, Size.X - 14), 10), HorizontalAlignment.Left, Math.Max(1, Size.X - 14), 10, item != null ? p.Warning : p.Muted);
    }
}
