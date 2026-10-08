using System;
using System.Collections.Generic;
using System.Linq;
using Sidereal.Native;
using Sidereal.Native.Input;

public static class InventoryTransferTargetTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string message) { count++; if (!condition) throw new Exception(message); }
        ItemDefinition Definition(int width = 2, int height = 3, ulong revision = 1) =>
            new("definition", "Item", width, height, 999, "back", "utility", "", "", "common", revision);
        InventoryItemView Item(string id, string container = "pockets", string equipment = "", bool rotated = false,
            ulong? scoped = 2, ItemDefinition? definition = null) =>
            new(id, "definition", container, equipment, 0, 0, rotated, definition ?? Definition(), scoped);
        InventoryContainerView Container(string id, string parent = "", bool carried = true, bool scoped = false,
            ulong? revision = 3, int width = 8, int height = 8) =>
            new(id, parent, "grid", "Same storage name", width, height, .01, 0, 0, "", carried, "", revision, scoped);
        var moving = Item("moving");
        var pockets = Container("pockets");
        var packItem = Item("pack", "", "back");
        var pack = Container("pack-grid", "pack");
        InventorySnapshot Snapshot(IEnumerable<InventoryItemView>? items = null, IEnumerable<InventoryContainerView>? containers = null) =>
            new(7, true, 999, .01, "pockets", (items ?? new[] { moving, packItem }).ToArray(),
                (containers ?? new[] { pockets, pack }).ToArray(), new Dictionary<byte, string>());
        var baseline = Snapshot();
        InventoryTransferEligibility Result(InventorySnapshot snapshot, string destination = "pack-grid", string item = "moving") =>
            InventoryTransferTarget.Evaluate(snapshot, item, destination);
        void Block(InventorySnapshot snapshot, string reason, string destination = "pack-grid", string item = "moving")
        {
            var result = Result(snapshot, destination, item);
            Check(!result.Ready && result.SpatialPlacement == null, $"Accepted blocked target: {reason}");
            Check(result.ContainerId == destination && result.Reason.Contains(reason, StringComparison.OrdinalIgnoreCase),
                $"Wrong target/reason for {reason}: {result.Reason}");
        }

        var ready = Result(baseline);
        Check(ready.Ready && ready.ContainerId == "pack-grid" && ready.SpatialPlacement == (0, 0, false), "Explicit destination/first placement changed");
        Check(ready.Reason.Contains("server", StringComparison.OrdinalIgnoreCase), "Spatial advice claims full capacity");
        Check(baseline.Revision == 7 && baseline.Items[0] == moving && moving.ContainerId == "pockets", "Advice mutated accepted state");
        Block(baseline with { Available = false }, "unavailable");
        Block(baseline, "no longer disclosed", item: "absent");
        Block(baseline, "access changed", destination: "hidden");
        Block(baseline, "access changed", destination: "");
        Block(baseline, "already in", destination: "pockets");
        Block(Snapshot(new[] { moving with { Definition = null }, packItem }), "pinned footprint");
        Block(Snapshot(new[] { moving with { Definition = Definition(revision: 0) }, packItem }), "pinned footprint");
        Block(Snapshot(new[] { moving with { Definition = Definition(width: 0) }, packItem }), "pinned footprint");
        Block(Snapshot(new[] { moving with { Definition = Definition() with { Id = "another-pin" } }, packItem }), "pinned footprint");
        Block(Snapshot(containers: new[] { pockets, pack with { Kind = "liquid" } }), "does not accept");
        Block(Snapshot(containers: new[] { pockets, pack with { PlacementId = "ground:actor:item" } }), "pickup sources");
        Block(Snapshot(new[] { moving with { ContainerId = "missing" }, packItem }), "source storage");
        Block(Snapshot(new[] { moving with { ContainerId = "" }, packItem }), "accepted item location");
        Check(Result(Snapshot(new[] { moving with { ContainerId = "", EquipmentSlot = "hand" }, packItem })).Ready,
            "Legacy equipped item cannot stow into a personal tab");

        Block(Snapshot(containers: new[] { pockets, pack with { ParentItemId = "moving" } }), "cannot contain itself");
        var bag = Item("bag");
        var descendant = Container("descendant", "bag");
        Block(Snapshot(new[] { moving, bag with { ContainerId = "moving-grid" } },
            new[] { pockets, Container("moving-grid", "moving"), descendant }), "cannot contain itself", "descendant");
        Block(Snapshot(containers: new[] { pockets, pack with { ParentItemId = "hidden-parent" } }), "parent storage item");
        Block(Snapshot(new[] { moving, packItem with { ContainerId = "undisclosed", EquipmentSlot = "" } }), "parent storage access");
        var cycleA = Item("a", "b-grid"); var cycleB = Item("b", "a-grid");
        Block(Snapshot(new[] { moving, cycleA, cycleB }, new[] { pockets, Container("a-grid", "a"), Container("b-grid", "b") }),
            "cycle", "a-grid");
        Block(Snapshot(new[] { moving with { ContainerId = "a-grid" }, cycleA, cycleB },
            new[] { pockets, pack, Container("a-grid", "a"), Container("b-grid", "b") }), "cycle");
        Block(Snapshot(new[] { moving, packItem, bag with { ContainerId = "" } }, new[] { pockets, pack, descendant }),
            "parent storage access", "descendant");
        Check(Result(Snapshot(new[] { moving, packItem, bag with { ContainerId = "pack-grid" } },
            new[] { pockets, pack, descendant }), "descendant").Ready, "Legitimate nested storage refused");

        var unknown = Item("unknown", "pack-grid") with { Definition = null, X = 99 };
        Block(Snapshot(new[] { moving, packItem, unknown }), "occupied pinned footprint");
        Block(Snapshot(new[] { moving, packItem, unknown with { Definition = Definition(revision: 0) } }), "occupied pinned footprint");
        Block(Snapshot(new[] { moving, packItem, unknown with { Definition = Definition() with { Id = "another-pin" } } }), "occupied pinned footprint");
        Block(Snapshot(containers: new[] { pockets, pack with { Width = 1, Height = 1 } }), "spatial room");
        var blocker = Item("blocker", "pack-grid", definition: Definition(8, 8));
        Block(Snapshot(new[] { moving, packItem, blocker }), "spatial room");
        var rotated = moving with { Rotated = true };
        Check(Result(Snapshot(new[] { rotated, packItem })).SpatialPlacement == (0, 0, true), "Automatic route ignored accepted rotation");
        Check(Result(Snapshot(new[] { rotated, packItem }, new[] { pockets, pack with { Width = 2, Height = 3 } })).SpatialPlacement == (0, 0, false),
            "Automatic route did not try accepted inverse rotation");
        var rowBlock = Item("row", "pack-grid", definition: Definition(2, 3));
        Check(Result(Snapshot(new[] { moving, packItem, rowBlock })).SpatialPlacement == (2, 0, false), "First-fit lost row-major order");

        var cargo = Container("cargo", carried: false, scoped: true);
        Check(Result(Snapshot(containers: new[] { pockets, pack, cargo }), "cargo").Ready, "Disclosed scoped route with revisions refused");
        Block(Snapshot(new[] { moving with { ScopedRevision = null }, packItem }, new[] { pockets, pack, cargo }), "revisions", "cargo");
        Block(Snapshot(containers: new[] { pockets with { ScopedRevision = null }, pack, cargo }), "revisions", "cargo");
        Block(Snapshot(containers: new[] { pockets, pack, cargo with { ScopedRevision = null } }), "revisions", "cargo");
        Block(Snapshot(new[] { moving with { ContainerId = "", EquipmentSlot = "hand" }, packItem }, new[] { pockets, pack, cargo }),
            "stow", "cargo");
        Check(Result(Snapshot(new[] { moving with { ContainerId = "cargo" }, packItem }, new[] { pockets, pack, cargo })).Ready,
            "Scoped-to-carried route refused");
        Check(Result(Snapshot(new[] { moving with { ScopedRevision = 0 }, packItem },
            new[] { pockets with { ScopedRevision = 0 }, pack, cargo with { ScopedRevision = 0 } }), "cargo").Ready,
            "A disclosed zero revision was mistaken for an absent token");
        var ground = pockets with { PlacementId = "ground:actor:item", Carried = false };
        Check(Result(Snapshot(containers: new[] { ground, pack })).Ready, "Ground wrapper stopped being a pickup source");
        Check(Result(baseline with { CarriedMassKg = double.MaxValue, CarryLimitKg = 0 }).Ready,
            "Spatial advice invented a mass/carry policy");

        Check(InventoryTransferTarget.IsPersonalContainer(baseline, pockets), "Root carried grid missing personal tab");
        Check(InventoryTransferTarget.IsPersonalContainer(baseline, pack), "Equipped backpack missing personal tab");
        Check(InventoryTransferTarget.PersonalLabel(baseline, pockets) == "Pockets" &&
            InventoryTransferTarget.PersonalLabel(baseline, pack) == "Backpack", "Source labels changed");
        var belt = Item("belt", "", "belt"); var beltGrid = Container("belt-grid", "belt", carried: false);
        Check(InventoryTransferTarget.IsPersonalContainer(Snapshot(new[] { moving, belt }, new[] { pockets, beltGrid }), beltGrid),
            "Any-equipped-parent predicate was replaced by backpack-only or carried-only");
        Check(InventoryTransferTarget.PersonalLabel(Snapshot(new[] { moving, belt }), beltGrid) == beltGrid.Name, "Equipped non-back parent lost actual label");
        Check(!InventoryTransferTarget.IsPersonalContainer(Snapshot(new[] { moving, packItem with { ContainerId = "pockets", EquipmentSlot = "" } }), pack),
            "Unequipped carried bag received a personal tab");
        Check(!InventoryTransferTarget.IsPersonalContainer(baseline, pockets with { Carried = false }), "World root received a personal tab");
        Check(!InventoryTransferTarget.IsPersonalContainer(baseline, pack with { ParentItemId = "hidden" }), "Hidden parent received a personal tab");
        Check(!InventoryTransferTarget.IsPersonalContainer(baseline, pockets with { Kind = "liquid" }), "Liquid received a personal tab");
        Check(!InventoryTransferTarget.IsPersonalContainer(baseline, ground), "Ground source received a personal tab");
        Check(!InventoryTransferTarget.IsPersonalContainer(baseline with { Available = false }, pockets), "Unavailable inventory received tabs");
        var duplicate = Container("second-root");
        Check(InventoryTransferTarget.IsPersonalContainer(baseline, duplicate) && duplicate.Name == pockets.Name,
            "Identical labels erased exact distinct container identities");
        Check(Result(Snapshot(containers: new[] { pockets, pack, duplicate }), "second-root").ContainerId == "second-root",
            "Named UUID was redirected by a duplicate label");
        Check(Result(Snapshot(containers: new[] { pockets, pack with { Name = "Renamed" } })).ContainerId == "pack-grid",
            "Rename changed destination UUID");
        Console.WriteLine($"Inventory transfer target tests: {count} independent assertions passed.");
    }
}
