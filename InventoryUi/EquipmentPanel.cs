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
    private readonly PaperDoll silhouette;
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
        silhouette = new PaperDoll { CustomMinimumSize = new Vector2(112, 344), SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddChild(left); body.AddChild(silhouette); body.AddChild(right);
        slots = InventorySnapshot.EquipmentSlots.Select(slot => new EquipmentSlot(core, demo, slot)).ToArray();
        for (var i = 0; i < slots.Length; i++) (i < 5 ? left : right).AddChild(slots[i]);
        var note = UiKit.Label("Silhouette preview · authored character model pending\nDrag matching equipment onto a slot. Drag it back to storage to stow it.", 12);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart; note.SizeFlagsHorizontal = SizeFlags.ExpandFill; AddChild(note);
        Refresh();
    }
    public void Refresh()
    {
        var snapshot = InventoryPresentation.Read(core, demo);
        summary.Text = demo ? "UI demo · local equipment specimens" : snapshot.Available ? $"{snapshot.Items.Count(i => i.EquipmentSlot.Length > 0)} equipped items    ·    Revision {snapshot.Revision}" : "Enter the world to load your equipped items.";
        foreach (var slot in slots) slot.Refresh(snapshot);
        silhouette.Snapshot = snapshot; silhouette.QueueRedraw();
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
    public EquipmentSlot(ClientCore core, bool demo, string slot)
    {
        this.core = core; this.demo = demo; this.slot = slot;
        CustomMinimumSize = new Vector2(132, 53); SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Stop;
        MouseEntered += () => { hover = true; QueueRedraw(); };
        MouseExited += () => { hover = false; QueueRedraw(); };
    }
    public void Refresh(InventorySnapshot next)
    {
        snapshot = next; item = snapshot.Items.FirstOrDefault(i => i.EquipmentSlot == slot);
        TooltipText = item != null ? ItemDrag.Tooltip(item) : $"{slot}: empty\nDrag an equippable item for this slot here.";
        QueueRedraw();
    }
    public override bool _CanDropData(Vector2 position, Variant data) => ItemDrag.Payload(data) && ItemDrag.Current is { } drag && drag.Core == core && drag.Demo == demo && !core.InventoryPending && drag.Item.Definition?.EquipSlot == slot;
    public override GodotObject _MakeCustomTooltip(string forText) => UiKit.Tooltip(item?.Name ?? char.ToUpperInvariant(slot[0]) + slot[1..], item == null ? forText : ItemDrag.Tooltip(item).Split('\n', 2).ElementAtOrDefault(1) ?? "");
    public override void _DropData(Vector2 position, Variant data)
    {
        if (!_CanDropData(position, data) || ItemDrag.Current is not { } drag) return;
        if (demo) DemoInventory.Equip(drag.Item.Id); else core.EquipItem(drag.Item.Id, drag.Revision);
    }
    public override Variant _GetDragData(Vector2 position)
    {
        if (item?.Definition == null || core.InventoryPending) return default;
        var drag = new ItemDrag { Core = core, Item = item, Revision = snapshot.Revision, Rotated = item.Rotated, Demo = demo };
        ItemDrag.Current = drag; drag.Preview = new InventoryDragPreview(drag); SetDragPreview(drag.Preview); core.ReleaseControls();
        return new Godot.Collections.Dictionary { ["sidereal-item"] = item.Id };
    }
    public override void _Notification(int what) { if (what == NotificationDragEnd) ItemDrag.Current = null; }
    public override void _Draw()
    {
        var p = SiderealPalette.Current; var font = GetThemeFont("font", "Label");
        var color = item != null ? p.Rarity(item.Definition?.Rarity ?? "common") : p.Accent;
        DrawRect(new Rect2(Vector2.Zero, Size), p.Surface with { A = p.Opacity });
        DrawRect(new Rect2(Vector2.One, Size - Vector2.One * 2), color with { A = hover ? .95f : .42f }, false, hover ? 2 : 1);
        DrawString(font, new Vector2(10, 18), char.ToUpperInvariant(slot[0]) + slot[1..], HorizontalAlignment.Left, Size.X - 20, 12, p.Accent);
        var texture = InventoryIcons.Texture(item?.Definition);
        var available = Size.X - (texture != null ? 55 : 20);
        DrawString(font, new Vector2(10, 39), InventoryGrid.Fit(font, item?.Name ?? "Empty", available, 13), HorizontalAlignment.Left, available, 13, item != null ? p.Text : p.Muted);
        if (texture != null) DrawTextureRect(texture, InventoryIcons.Fit(texture, new Rect2(Size.X - 44, 17, 36, 31)), false);
    }
}

internal partial class PaperDoll : Control
{
    public InventorySnapshot Snapshot = InventorySnapshot.Empty;
    public PaperDoll() { MouseFilter = MouseFilterEnum.Ignore; }
    public override void _Draw()
    {
        var p = SiderealPalette.Current; var center = Size.X * .5f;
        var scale = Math.Min(Size.Y / 330, Size.X / 112);
        Vector2 Point(float x, float y) => new(center + x * scale, y * scale + 8);
        void Part(string slot, Vector2[] points)
        {
            var equipped = Snapshot.Items.Any(i => i.EquipmentSlot == slot);
            DrawColoredPolygon(points, p.Accent with { A = equipped ? .2f : .045f });
            DrawPolyline(points.Append(points[0]).ToArray(), p.Accent with { A = equipped ? .85f : .28f }, 1.5f, true);
        }
        Part("helmet", new[] { Point(-17, 13), Point(17, 13), Point(22, 38), Point(13, 57), Point(-13, 57), Point(-22, 38) });
        Part("chest", new[] { Point(-25, 70), Point(25, 70), Point(29, 114), Point(19, 169), Point(-19, 169), Point(-29, 114) });
        Part("belt", new[] { Point(-20, 169), Point(20, 169), Point(24, 183), Point(-24, 183) });
        foreach (var direction in new[] { -1, 1 })
        {
            Part("shoulders", new[] { Point(direction * 27, 73), Point(direction * 41, 79), Point(direction * 48, 108), Point(direction * 33, 112) });
            Part("gloves", new[] { Point(direction * 43, 109), Point(direction * 50, 157), Point(direction * 45, 178), Point(direction * 34, 177), Point(direction * 34, 137) });
            Part("legs", new[] { Point(direction * 3, 185), Point(direction * 25, 185), Point(direction * 25, 233), Point(direction * 20, 281), Point(direction * 4, 281) });
            Part("boots", new[] { Point(direction * 4, 281), Point(direction * 21, 281), Point(direction * 26, 307), Point(direction * 27, 314), Point(direction * 3, 314) });
        }
        DrawLine(Point(-42, 326), Point(42, 326), p.Accent with { A = .45f }, 1);
    }
}
