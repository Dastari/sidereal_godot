using System;
using Godot;
using Sidereal.Native;
using Sidereal.Ui;

namespace Sidereal.InventoryUi;

public partial class HotbarView : HBoxContainer
{
    private readonly ClientCore core;
    private readonly bool demo;
    private readonly HotbarSlot[] slots;
    public HotbarView(ClientCore core, bool demo)
    {
        this.core = core; this.demo = demo;
        InventoryPresentation.EnsureCatalogue();
        AddThemeConstantOverride("separation", 7);
        slots = new HotbarSlot[5];
        for (byte slot = 0; slot < 5; slot++) { slots[slot] = new HotbarSlot(core, demo, slot); AddChild(slots[slot]); }
        Refresh();
    }
    public void Refresh() { var snapshot = InventoryPresentation.Read(core, demo); foreach (var slot in slots) slot.Refresh(snapshot); }
    public override void _UnhandledInput(InputEvent input)
    {
        if (!IsVisibleInTree() || input is not InputEventKey { Pressed: true, Echo: false } key || GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit) return;
        var number = (long)key.Keycode;
        if (number < '1' || number > '5') return;
        var slot = (byte)(number - '1');
        var snapshot = InventoryPresentation.Read(core, demo);
        if (demo) { if (snapshot.Hotbar.TryGetValue(slot, out var item)) DemoInventory.Equip(item); }
        else if (snapshot.Hotbar.TryGetValue(slot, out var item) && item.Length > 0) core.ActivateHotbar(slot, snapshot.Revision);
        GetViewport().SetInputAsHandled();
    }
}

internal partial class HotbarSlot : Control
{
    private readonly ClientCore core;
    private readonly bool demo;
    private readonly byte slot;
    private InventorySnapshot snapshot = InventorySnapshot.Empty;
    private InventoryItemView? item;
    private bool hover;
    public HotbarSlot(ClientCore core, bool demo, byte slot)
    {
        this.core = core; this.demo = demo; this.slot = slot;
        CustomMinimumSize = new Vector2(76, 64); MouseFilter = MouseFilterEnum.Stop;
        MouseEntered += () => { hover = true; QueueRedraw(); };
        MouseExited += () => { hover = false; QueueRedraw(); };
    }
    public void Refresh(InventorySnapshot next)
    {
        snapshot = next; item = next.Hotbar.TryGetValue(slot, out var id) ? next.Item(id) : null;
        TooltipText = item != null ? ItemDrag.Tooltip(item) + $"\nPress {slot + 1} or click to equip. Right-click clears this reference." : $"Quick slot {slot + 1}\nDrag an equippable item here. This is a reference, not a second item.";
        QueueRedraw();
    }
    public override bool _CanDropData(Vector2 position, Variant data) => ItemDrag.Payload(data) && ItemDrag.Current is { } drag && drag.Core == core && drag.Demo == demo && !core.InventoryPending && drag.Item.Definition?.EquipSlot.Length > 0;
    public override GodotObject _MakeCustomTooltip(string forText) => UiKit.Tooltip(item?.Name ?? $"Quick slot {slot + 1}", forText);
    public override void _DropData(Vector2 position, Variant data)
    {
        if (!_CanDropData(position, data) || ItemDrag.Current is not { } drag) return;
        if (demo) DemoInventory.Assign(slot, drag.Item.Id); else core.AssignHotbar(slot, drag.Item.Id, drag.Revision);
    }
    public override void _GuiInput(InputEvent input)
    {
        if (input is not InputEventMouseButton { Pressed: true } mouse) return;
        if (mouse.ButtonIndex == MouseButton.Right)
        { if (demo) DemoInventory.Assign(slot, ""); else core.AssignHotbar(slot, "", snapshot.Revision); AcceptEvent(); }
        else if (mouse.ButtonIndex == MouseButton.Left && item != null)
        { if (demo) DemoInventory.Equip(item.Id); else core.ActivateHotbar(slot, snapshot.Revision); AcceptEvent(); }
    }
    public override void _Draw()
    {
        var p = SiderealPalette.Current; var font = GetThemeFont("font", "Label");
        var color = item != null ? p.Rarity(item.Definition?.Rarity ?? "common") : p.Accent;
        DrawRect(new Rect2(Vector2.Zero, Size), p.Surface with { A = p.Opacity });
        DrawRect(new Rect2(Vector2.One, Size - Vector2.One * 2), color with { A = hover ? 1 : .52f }, false, hover ? 2 : 1);
        DrawString(font, new Vector2(7, 16), (slot + 1).ToString(), HorizontalAlignment.Left, -1, 12, p.Accent);
        var icon = item?.Definition?.Category switch { "weapon" => "╱", "medical" => "+", "tool" => "◇", _ => "□" };
        if (InventoryIcons.Texture(item?.Definition) is { } texture) DrawTextureRect(texture, InventoryIcons.Fit(texture, new Rect2(17, 9, Size.X - 34, 36)), false);
        else DrawString(font, new Vector2(Size.X * .42f, 40), item != null ? icon : "—", HorizontalAlignment.Left, -1, 23, color);
        DrawString(font, new Vector2(5, 56), InventoryGrid.Fit(font, item?.Name ?? "Empty", Size.X - 10, 10), HorizontalAlignment.Left, Size.X - 10, 10, item != null ? p.Text : p.Muted);
        if (!demo && core.InventoryPending) DrawRect(new Rect2(Vector2.Zero, Size), p.Surface with { A = .4f });
    }
}
