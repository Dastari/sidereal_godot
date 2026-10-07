using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sidereal.Bindings;

namespace Sidereal.Native;

public sealed record ItemDefinition(string Id, string Name, int Width, int Height, double MassKg,
    string EquipSlot, string Category, string Role, string IconUrl, string Rarity, ulong Revision)
{
    public (int Width, int Height) Footprint(bool rotated) => rotated ? (Height, Width) : (Width, Height);
}

public sealed record InventoryItemView(string Id, string DefinitionId, string ContainerId, string EquipmentSlot,
    int X, int Y, bool Rotated, ItemDefinition? Definition, ulong? ScopedRevision = null)
{
    public string Name => Definition?.Name ?? DefinitionId;
}

public sealed record InventoryContainerView(string Id, string ParentItemId, string Kind, string Name,
    int Width, int Height, double MaxMassKg, double CapacityLitres, double AmountLitres, string LiquidType, bool Carried,
    string PlacementId = "", ulong? ScopedRevision = null, bool IsScopedCargo = false);

public sealed record InventorySnapshot(ulong Revision, bool Available, double CarriedMassKg, double CarryLimitKg,
    string PocketsId, IReadOnlyList<InventoryItemView> Items, IReadOnlyList<InventoryContainerView> Containers,
    IReadOnlyDictionary<byte, string> Hotbar, bool IsDemo = false)
{
    public static readonly string[] EquipmentSlots = { "helmet", "visor", "chest", "shoulders", "gloves", "belt", "legs", "boots", "back", "uniform", "hand" };
    public static readonly InventorySnapshot Empty = new(0, false, 0, 0, "", Array.Empty<InventoryItemView>(), Array.Empty<InventoryContainerView>(), new Dictionary<byte, string>());
    public InventoryItemView? Item(string id) => Items.FirstOrDefault(i => i.Id == id);
    public InventoryContainerView? Container(string id) => Containers.FirstOrDefault(c => c.Id == id);
    public bool UsesScopedCargo(string itemId, string destinationId) =>
        Item(itemId) is { ContainerId.Length: > 0 } item && Container(item.ContainerId)?.IsScopedCargo == true ||
        Container(destinationId)?.IsScopedCargo == true;

    public (int X, int Y, bool Rotated)? FirstPlacement(string itemId, string containerId)
    {
        var item = Item(itemId);
        var container = Container(containerId);
        if (item?.Definition == null || container is not { Kind: "grid" }) return null;
        foreach (var rotated in new[] { item.Rotated, !item.Rotated }.Distinct())
        for (var y = 0; y < container.Height; y++)
        for (var x = 0; x < container.Width; x++)
            if (Fits(itemId, containerId, x, y, rotated)) return (x, y, rotated);
        return null;
    }

    // Spatial preview only. Access, mass, nesting, equipment swaps and revisions stay on the server.
    public bool Fits(string itemId, string containerId, int x, int y, bool rotated)
    {
        var item = Item(itemId);
        var container = Container(containerId);
        if (item?.Definition is not { } definition || container == null || container.Kind != "grid") return false;
        var (w, h) = definition.Footprint(rotated);
        if (x < 0 || y < 0 || x > container.Width - w || y > container.Height - h) return false;
        foreach (var other in Items.Where(i => i.ContainerId == containerId && i.Id != itemId))
        {
            // Unknown footprints must block placement rather than silently treating occupied space as empty.
            if (other.Definition == null) return false;
            var (ow, oh) = other.Definition.Footprint(other.Rotated);
            if (x < other.X + ow && other.X < x + w && y < other.Y + oh && other.Y < y + h) return false;
        }
        return true;
    }

    public static InventorySnapshot Demo()
    {
        ItemDefinition Def(string id, string name, int w, int h, string slot, string category, string rarity, double mass) =>
            new(id, name, w, h, mass, slot, category, "Interface specimen", "", rarity, 1);
        var definitions = new[] {
            Def("demo-rifle", "Pulse carbine", 2, 4, "hand", "weapon", "legendary", 3.2),
            Def("demo-tool", "Multi-tool", 2, 2, "hand", "tool", "rare", 1.2),
            Def("demo-medkit", "Field medkit", 2, 2, "hand", "medical", "uncommon", .8),
            Def("demo-battery", "Power cell", 1, 2, "", "utility", "common", .5),
            Def("demo-helmet", "Explorer helmet", 2, 2, "helmet", "armor", "epic", 1.4),
            Def("demo-backpack", "Expedition pack", 2, 3, "back", "utility", "rare", 2.5),
            Def("demo-suit", "Explorer suit", 2, 3, "uniform", "armor", "common", 1.1)
        };
        InventoryItemView Item(int d, int x, int y, string container = "demo-pockets", string slot = "", bool rotated = false) =>
            new("ui-demo-" + d, definitions[d].Id, container, slot, x, y, rotated, definitions[d]);
        return new(42, true, 10.7, 35, "demo-pockets",
            new[] { Item(0, 0, 0), Item(1, 2, 0), Item(2, 2, 2), Item(3, 4, 0), Item(4, 0, 0, "", "helmet"), Item(5, 0, 0, "", "back"), Item(6, 0, 0, "", "uniform") },
            new[] { new InventoryContainerView("demo-pockets", "", "grid", "Pockets", 8, 6, 15, 0, 0, "", true),
                new InventoryContainerView("demo-pack", "ui-demo-5", "grid", "Expedition pack", 8, 8, 25, 0, 0, "", true) },
            new Dictionary<byte, string> { [0] = "ui-demo-0", [1] = "ui-demo-1", [2] = "ui-demo-2", [3] = "ui-demo-3", [4] = "" }, true);
    }
}

public static class InventoryCatalog
{
    private static IReadOnlyDictionary<string, ItemDefinition> seeds = new Dictionary<string, ItemDefinition>();
    private static readonly Dictionary<string, ItemDefinition?> cache = new();
    public static void LoadRevisionOneSeed(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("source").GetProperty("revision").GetInt32() != 1)
            throw new InvalidOperationException("The item presentation fallback must be revision one.");
        seeds = document.RootElement.GetProperty("definitions").EnumerateArray()
            .Select(d => Parse(d, 1)).ToDictionary(d => d.Id);
    }

    public static ItemDefinition? Resolve(string definitionId, ulong revision, IEnumerable<PublishedItemDefinition> rows)
    {
        var reference = $"item:{definitionId}@{revision}";
        var row = rows.FirstOrDefault(r => r.DefinitionRef == reference && r.Kind == "item" && r.DefinitionId == definitionId && r.Revision == revision && r.Status is "published" or "retired");
        if (row == null) return revision == 1 && seeds.TryGetValue(definitionId, out var seed) ? seed : null;
        var key = reference + "#" + row.Sha256;
        if (cache.TryGetValue(key, out var cached)) return cached;
        if (cache.Count > 4096) cache.Clear();
        ItemDefinition? result;
        try
        {
            using var document = JsonDocument.Parse(row.PayloadJson);
            result = Parse(document.RootElement, revision);
            if (result.Id != definitionId) result = null;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException) { result = null; }
        cache[key] = result;
        return result;
    }

    private static ItemDefinition Parse(JsonElement d, ulong revision)
    {
        string Text(string key, string fallback = "") => d.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
        var w = d.GetProperty("width").GetInt32(); var h = d.GetProperty("height").GetInt32();
        var mass = d.GetProperty("massKg").GetDouble();
        if (w < 1 || h < 1 || w > 64 || h > 64 || !double.IsFinite(mass) || mass < 0) throw new InvalidOperationException("Invalid item presentation dimensions.");
        return new(Text("id"), Text("name"), w, h, mass, Text("equipSlot"), Text("category", "equipment"), Text("role"), Text("iconUrl"), Text("rarity", "common"), revision);
    }

    public static InventorySnapshot Read(DbConnection? connection)
    {
        if (connection == null) return InventorySnapshot.Empty;
        var state = connection.Db.OwnInventoryState.Iter().FirstOrDefault();
        if (state == null) return InventorySnapshot.Empty;
        var definitions = connection.Db.PublishedItemDefinitions.Iter().ToArray();
        var pins = connection.Db.OwnItemDefinitionPins.Iter().ToDictionary(p => p.ItemId);
        var carriedRevisions = connection.Db.OwnCarriedInventoryRevisions.Iter().ToArray();
        ulong? CarriedRevision(string kind, string id) => carriedRevisions.FirstOrDefault(r => r.Kind == kind && r.Id == id)?.Revision;
        var items = connection.Db.OwnInventoryItems.Iter().Select(i => {
            var revision = pins.TryGetValue(i.Id, out var pin) && pin.DefinitionId == i.DefinitionId ? pin.ItemRevision : 1;
            return new InventoryItemView(i.Id, i.DefinitionId, i.ContainerId, i.EquipmentSlot, i.X, i.Y, i.Rotated, Resolve(i.DefinitionId, revision, definitions), CarriedRevision("item", i.Id));
        }).Concat(connection.Db.OwnReachableCargoItems.Iter().Select(i => {
            var revision = pins.TryGetValue(i.Id, out var pin) && pin.DefinitionId == i.DefinitionId ? pin.ItemRevision : 1;
            return new InventoryItemView(i.Id, i.DefinitionId, i.ContainerId, "", i.X, i.Y, i.Rotated, Resolve(i.DefinitionId, revision, definitions), i.Revision);
        })).DistinctBy(i => i.Id).ToArray();
        var containers = connection.Db.OwnInventoryContainers.Iter().Select(c => new InventoryContainerView(c.Id, c.ParentItemId, c.Kind, c.Name, checked((int)c.Width), checked((int)c.Height), c.MaxMassKg, c.CapacityLitres, c.AmountLitres, c.LiquidType, c.Carried, c.PlacementId, CarriedRevision("container", c.Id)))
            .Concat(connection.Db.OwnReachableCargoContainers.Iter().Select(c => new InventoryContainerView(c.Id, c.ParentItemId, c.Kind, c.Name, checked((int)c.Width), checked((int)c.Height), c.MaxMassKg, c.CapacityLitres, c.AmountLitres, c.LiquidType, false, c.PlacedObjectId, c.Revision, true)))
            .DistinctBy(c => c.Id).ToArray();
        return new(state.Revision, true, state.CarriedMassKg, state.CarryLimitKg, state.PocketsId, items, containers,
            connection.Db.OwnInventoryHotbar.Iter().ToDictionary(h => h.Slot, h => h.ItemId));
    }
}
