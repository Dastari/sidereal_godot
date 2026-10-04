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
    private readonly ClientCore core;
    private readonly bool demo;
    private readonly Label summary, message, reservoir;
    private readonly Button starter;
    private readonly ContainerPane left, right;
    public bool InteractionActive => ItemDrag.Current?.Core == core;
    public InventoryWorkspace(ClientCore core, bool demo)
    {
        this.core = core; this.demo = demo;
        InventoryPresentation.EnsureCatalogue();
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 10);
        summary = UiKit.Label("Connect to load your inventory", 16); AddChild(summary);
        message = UiKit.Label("", 13); message.AutowrapMode = TextServer.AutowrapMode.WordSmart; message.SizeFlagsHorizontal = SizeFlags.ExpandFill; AddChild(message);
        starter = UiKit.Button("Claim starter equipment", () => { if (!demo) core.ClaimStarterKit(); }); AddChild(starter);
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 12); AddChild(columns);
        left = new ContainerPane(core, demo, 0); right = new ContainerPane(core, demo, 1);
        columns.AddChild(left); columns.AddChild(right);
        reservoir = UiKit.Label("", 13); reservoir.AutowrapMode = TextServer.AutowrapMode.WordSmart; AddChild(reservoir);
        var help = UiKit.Label("Drag to move · R to rotate · Double-click to equip\nGreen checks space; the server also validates mass, reach and nesting.", 12);
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart; help.SizeFlagsHorizontal = SizeFlags.ExpandFill; AddChild(help);
        Refresh();
    }
    public void Refresh()
    {
        var snapshot = InventoryPresentation.Read(core, demo);
        summary.Text = snapshot.Available ? $"Carried {snapshot.CarriedMassKg:0.##} / {snapshot.CarryLimitKg:0.##} kg    ·    Revision {snapshot.Revision}" : "Enter the world to load your private inventory.";
        message.Text = demo ? DemoInventory.Message : core.InventoryMessage;
        message.Modulate = (demo || !core.InventoryMessage.StartsWith("Rejected") ? SiderealPalette.Current.Muted : SiderealPalette.Current.Warning);
        starter.Visible = !demo && core.Connection != null && !snapshot.Available;
        left.Refresh(snapshot); right.Refresh(snapshot);
        var liquids = snapshot.Containers.Where(c => c.Kind == "liquid").ToArray();
        reservoir.Text = string.Join("\n", liquids.Select(c => $"{c.Name}: {c.AmountLitres:0.##} / {c.CapacityLitres:0.##} L {c.LiquidType}"));
    }
}

internal partial class ContainerPane : VBoxContainer
{
    private readonly ClientCore core;
    private readonly bool demo;
    private readonly int initialIndex;
    private readonly OptionButton choice;
    private readonly Label capacity;
    private InventoryGrid? grid;
    private InventorySnapshot snapshot = InventorySnapshot.Empty;
    private string[] ids = Array.Empty<string>();
    private string chosen = "";
    public ContainerPane(ClientCore core, bool demo, int initialIndex)
    {
        this.core = core; this.demo = demo; this.initialIndex = initialIndex;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(280, 0);
        AddThemeConstantOverride("separation", 7);
        choice = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Only storage the server allows you to access is listed." };
        AddChild(choice);
        choice.ItemSelected += index => { chosen = ids[(int)index]; ReplaceGrid(); choice.ReleaseFocus(); };
        capacity = UiKit.Label("", 12); AddChild(capacity);
    }
    public void Refresh(InventorySnapshot next)
    {
        snapshot = next;
        var containers = next.Containers.Where(c => c.Kind == "grid").OrderByDescending(c => c.Id == next.PocketsId).ThenByDescending(c => c.Carried).ThenBy(c => c.Name).ToArray();
        var nextIds = containers.Select(c => c.Id).ToArray();
        if (!nextIds.SequenceEqual(ids))
        {
            ids = nextIds; choice.Clear();
            foreach (var c in containers) choice.AddItem(c.Name + (c.Carried ? " · carried" : " · nearby"));
            if (!ids.Contains(chosen)) chosen = ids.ElementAtOrDefault(Math.Min(initialIndex, Math.Max(0, ids.Length - 1))) ?? "";
            if (ids.Length > 0) choice.Select(Array.IndexOf(ids, chosen));
            ReplaceGrid();
        }
        if (next.Container(chosen) is { } container) capacity.Text = $"{container.Width} × {container.Height} cells    ·    Payload limit {container.MaxMassKg:0.##} kg";
        else capacity.Text = "No accessible storage.";
        grid?.Refresh(next);
    }
    private void ReplaceGrid()
    {
        if (grid != null) { RemoveChild(grid); grid.QueueFree(); grid = null; }
        if (snapshot.Container(chosen) is not { } container) return;
        grid = new InventoryGrid(core, demo, snapshot, container); AddChild(grid);
    }
}
