using System;
using System.Linq;
using Godot;
using Sidereal.Ui;
using Sidereal.Native;

namespace Sidereal.InventoryUi;

internal sealed class ItemDrag
{
    public static ItemDrag? Current;
    public required ClientCore Core;
    public required InventoryItemView Item;
    public required ulong Revision;
    public bool Rotated, Demo;
    public InventoryDragPreview? Preview;
    public static bool Payload(Variant data) => data.VariantType == Variant.Type.Dictionary && data.AsGodotDictionary().ContainsKey("sidereal-item");
    public static string Tooltip(InventoryItemView item) => item.Definition is { } d
        ? $"{d.Name}\n{d.Role}\n{d.Width} × {d.Height} cells · {d.MassKg:0.##} kg dry mass\nDefinition revision {d.Revision}" + (d.EquipSlot.Length > 0 ? $"\nEquipment slot: {d.EquipSlot}" : "")
        : $"{item.DefinitionId}\nThis pinned definition is unavailable. Update the client or reconnect. Packing is disabled until its footprint is known.";
}

internal partial class InventoryDragPreview : Control
{
    private readonly ItemDrag drag;
    public InventoryDragPreview(ItemDrag drag) { this.drag = drag; MouseFilter = MouseFilterEnum.Ignore; UpdateSize(); }
    public void UpdateSize()
    {
        var (w, h) = drag.Item.Definition!.Footprint(drag.Rotated);
        Size = CustomMinimumSize = new Vector2(w * 40, h * 40);
        QueueRedraw();
    }
    public override void _Draw()
    {
        var p = SiderealPalette.Current;
        DrawRect(new Rect2(Vector2.Zero, Size), p.Surface with { A = .95f });
        DrawRect(new Rect2(Vector2.One, Size - Vector2.One * 2), p.Rarity(drag.Item.Definition!.Rarity), false, 2);
        DrawString(GetThemeFont("font", "Label"), new Vector2(8, 25), "R · rotate", HorizontalAlignment.Left, Size.X - 16, 13, p.Text);
        if (InventoryIcons.Texture(drag.Item.Definition) is { } texture)
        {
            var bounds = new Rect2(4, 29, Size.X - 8, Math.Max(1, Size.Y - 33));
            var turns = InventoryIcons.QuarterTurns(drag.Item.Definition!, texture, drag.Rotated);
            DrawSetTransform(bounds.GetCenter(), turns * Mathf.Pi / 2);
            var local = turns % 2 == 1 ? new Vector2(bounds.Size.Y, bounds.Size.X) : bounds.Size;
            DrawTextureRect(texture, InventoryIcons.Fit(texture, new Rect2(-local * .5f, local)), false);
            DrawSetTransform(Vector2.Zero);
        }
    }
}

public partial class InventoryGrid : Control
{
    private readonly ClientCore core;
    private readonly bool demo;
    private InventorySnapshot snapshot;
    private InventoryContainerView container;
    private string hovered = "";
    private bool previewFits;
    private Vector2I previewCell;
    private float Cell => Math.Clamp(Size.X / Math.Max(container.Width, 1), 32, 64);

    public InventoryGrid(ClientCore core, bool demo, InventorySnapshot snapshot, InventoryContainerView container)
    {
        this.core = core; this.demo = demo; this.snapshot = snapshot; this.container = container;
        MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        FocusMode = FocusModeEnum.All;
        CustomMinimumSize = new Vector2(container.Width * 36, container.Height * 42);
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
        // The drawn pitch follows expanded width. Its full last row must also be inside
        // the Control's hit rectangle, including short four-column pockets.
        var height = container.Height * Cell;
        if (!Mathf.IsEqualApprox(CustomMinimumSize.Y, height))
            CustomMinimumSize = new Vector2(container.Width * 36, height);
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
        if (hovered != (item?.Id ?? "")) { hovered = item?.Id ?? ""; TooltipText = item == null ? "Drag an item here. R rotates while dragging." : ItemDrag.Tooltip(item); QueueRedraw(); }
        if (ItemDrag.Current is { } drag && GetRect().HasPoint(Position + mouse))
        {
            var cell = CellAt(mouse);
            var fits = snapshot.Fits(drag.Item.Id, container.Id, cell.X, cell.Y, drag.Rotated) && !core.InventoryPending;
            if (cell != previewCell || fits != previewFits) { previewCell = cell; previewFits = fits; QueueRedraw(); }
        }
    }

    public override void _Input(InputEvent input)
    {
        if (ItemDrag.Current is { } drag && drag.Core == core && input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R })
        {
            drag.Rotated = !drag.Rotated;
            drag.Preview?.UpdateSize();
            GetViewport().SetInputAsHandled();
            QueueRedraw();
        }
    }

    public override void _Notification(int what)
    { if (what == NotificationDragEnd) { ItemDrag.Current = null; QueueRedraw(); } }

    private Vector2I CellAt(Vector2 position) => new((int)Math.Floor(position.X / Cell), (int)Math.Floor(position.Y / Cell));
    private InventoryItemView? Hit(Vector2 position)
    {
        var cell = CellAt(position);
        return snapshot.Items.FirstOrDefault(i => {
            if (i.ContainerId != container.Id) return false;
            var (w, h) = i.Definition?.Footprint(i.Rotated) ?? (1, 1);
            return cell.X >= i.X && cell.X < i.X + w && cell.Y >= i.Y && cell.Y < i.Y + h;
        });
    }
    public override GodotObject _MakeCustomTooltip(string forText)
    {
        var item = snapshot.Item(hovered);
        return item == null ? UiKit.Tooltip("Inventory grid", forText) : UiKit.Tooltip(item.Name, ItemDrag.Tooltip(item).Split('\n', 2).ElementAtOrDefault(1) ?? "");
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        var item = Hit(atPosition);
        if (item?.Definition == null || core.InventoryPending) return default;
        var drag = new ItemDrag { Core = core, Item = item, Revision = snapshot.Revision, Rotated = item.Rotated, Demo = demo };
        ItemDrag.Current = drag;
        drag.Preview = new InventoryDragPreview(drag);
        SetDragPreview(drag.Preview);
        core.ReleaseControls();
        return new Godot.Collections.Dictionary { ["sidereal-item"] = item.Id };
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        if (!ItemDrag.Payload(data) || ItemDrag.Current is not { } drag || drag.Core != core || drag.Demo != demo || core.InventoryPending) return false;
        previewCell = CellAt(atPosition);
        previewFits = snapshot.Fits(drag.Item.Id, container.Id, previewCell.X, previewCell.Y, drag.Rotated);
        QueueRedraw();
        return previewFits;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (!_CanDropData(atPosition, data) || ItemDrag.Current is not { } drag) return;
        if (demo) { DemoInventory.Move(drag.Item.Id, container.Id, previewCell.X, previewCell.Y, drag.Rotated); return; }
        core.MoveItem(drag.Item.Id, container.Id, previewCell.X, previewCell.Y, drag.Rotated, drag.Revision);
    }

    public override void _GuiInput(InputEvent input)
    {
        if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left, DoubleClick: true } mouse && Hit(mouse.Position) is { Definition: { EquipSlot.Length: > 0 } } item)
        { if (demo) DemoInventory.Equip(item.Id); else core.EquipItem(item.Id, snapshot.Revision); AcceptEvent(); }
    }

    public override void _Draw()
    {
        var p = SiderealPalette.Current;
        var pitch = Cell;
        var font = GetThemeFont("font", "Label");
        for (var y = 0; y < container.Height; y++) for (var x = 0; x < container.Width; x++)
        {
            var rect = new Rect2(x * pitch + 1, y * pitch + 1, pitch - 2, pitch - 2);
            DrawRect(rect, p.Surface with { A = Math.Clamp(p.Opacity + .06f, .1f, .98f) });
            DrawRect(rect, p.Accent with { A = .18f }, false, 1);
        }
        foreach (var item in snapshot.Items.Where(i => i.ContainerId == container.Id))
        {
            var (w, h) = item.Definition?.Footprint(item.Rotated) ?? (1, 1);
            var rect = new Rect2(item.X * pitch + 3, item.Y * pitch + 3, w * pitch - 6, h * pitch - 6);
            var color = p.Rarity(item.Definition?.Rarity ?? "common");
            var alpha = ItemDrag.Current?.Item.Id == item.Id ? .26f : .78f;
            DrawRect(rect, p.Surface.Lerp(color, .13f) with { A = alpha });
            DrawRect(rect, color with { A = item.Id == hovered ? 1f : .65f }, false, item.Id == hovered ? 2 : 1);
            DrawLine(rect.Position + new Vector2(6, 5), rect.Position + new Vector2(Math.Min(30, rect.Size.X - 6), 5), color, 2);
            var label = Fit(font, item.Name, rect.Size.X - 12, 13);
            DrawString(font, rect.Position + new Vector2(6, rect.Size.Y - 8), label, HorizontalAlignment.Left, rect.Size.X - 12, 13, p.Text);
            // Silhouette glyphs are interface art, not a substitute for the item model or its physical bounds.
            var center = rect.GetCenter() + new Vector2(0, -7);
            var span = Math.Min(rect.Size.X, rect.Size.Y) * .24f;
            if (InventoryIcons.Texture(item.Definition) is { } texture)
            {
                var bounds = new Rect2(rect.Position + new Vector2(5, 6), new Vector2(rect.Size.X - 10, Math.Max(1, rect.Size.Y - 26)));
                var turns = InventoryIcons.QuarterTurns(item.Definition!, texture, item.Rotated);
                DrawSetTransform(bounds.GetCenter(), turns * Mathf.Pi / 2);
                var localSize = turns % 2 == 1 ? new Vector2(bounds.Size.Y, bounds.Size.X) : bounds.Size;
                DrawTextureRect(texture, InventoryIcons.Fit(texture, new Rect2(-localSize * .5f, localSize)), false, Colors.White with { A = ItemDrag.Current?.Item.Id == item.Id ? .35f : 1 });
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
        if (ItemDrag.Current is { } drag && drag.Core == core && new Rect2(Vector2.Zero, Size).HasPoint(GetLocalMousePosition()))
        {
            var (w, h) = drag.Item.Definition!.Footprint(drag.Rotated);
            var rect = new Rect2(previewCell.X * pitch, previewCell.Y * pitch, w * pitch, h * pitch);
            var color = previewFits ? p.Success : p.Danger;
            DrawRect(rect, color with { A = .24f }); DrawRect(rect, color, false, 2);
        }
    }

    internal static string Fit(Font font, string text, float width, int size)
    {
        if (font.GetStringSize(text, fontSize: size).X <= width) return text;
        while (text.Length > 1 && font.GetStringSize(text + "…", fontSize: size).X > width) text = text[..^1];
        return text + "…";
    }
}
