using System;
using System.Collections.Generic;
using System.Linq;
using Sidereal.Native;
using Sidereal.Native.Input;

public static class InventoryAttemptTests
{
    private const string Operation = "00000000-0000-4000-8000-000000000001";
    private const string EquipOperation = "00000000-0000-4000-8000-000000000002";
    private static readonly ItemDefinition Definition = new("pistol", "Pistol", 2, 2, 1, "hand", "weapon", "", "", "common", 3);
    private static readonly InventoryAttemptSource Source = new("item", "pistol", 3, "pistol", 5, "source", "", 0, 0, false, "cargo-placement", "instance", "deck");
    private static readonly InventoryAttemptTarget MoveTarget = new("destination", 2, 1, true, ItemId: "item");
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static InventorySnapshot Inventory(ulong revision, string container = "destination", string equipment = "", int x = 2, int y = 1, bool rotated = true,
        ulong itemRevision = 11, ulong sourceRevision = 21, ulong destinationRevision = 31, bool includeItem = true,
        bool includeSource = true, ItemDefinition? definition = null, string binding = "item")
    {
        var containers = new List<InventoryContainerView> { new("destination", "", "grid", "Carried", 8, 8, 50, 0, 0, "", true, ScopedRevision: destinationRevision),
            new("ground", "", "grid", "Floor", 4, 4, 50, 0, 0, "", false, ScopedRevision: 1) };
        if (includeSource) containers.Add(new("source", "", "grid", "Cargo", 8, 8, 50, 0, 0, "", false, "cargo-placement", sourceRevision, true));
        var items = includeItem ? new[] { new InventoryItemView("item", "pistol", container, equipment, x, y, rotated, definition ?? Definition, itemRevision) } : Array.Empty<InventoryItemView>();
        return new(revision, true, 1, 35, "destination", items, containers, new Dictionary<byte, string> { [0] = binding });
    }
    private static InventoryAttemptRows Rows(InventorySnapshot inventory, ulong session = 7, string actor = "actor", bool ground = false, ulong pinRevision = 3, ulong weaponRevision = 5)
        => new(session, actor, inventory, new Dictionary<string, (string, ulong, ulong)> { ["item"] = ("pistol", pinRevision, weaponRevision) },
            new HashSet<string>(ground ? new[] { "item" } : Array.Empty<string>()));
    private static InventoryAttemptState Begin(InventoryAttemptKind kind = InventoryAttemptKind.Move, InventoryAttemptTarget? target = null,
        bool cargo = false, bool composed = false, InventoryAttemptSource? source = null)
    {
        var state = new InventoryAttemptState();
        Require(state.Begin(7, "actor", kind, source ?? Source, cargo ? new(10, 10, 20, 30, true) : new(10), target ?? MoveTarget,
            Operation, 0, composed ? InventoryAttemptStage.CargoTransfer : InventoryAttemptStage.Single,
            composed ? MoveTarget : null), "Could not begin test attempt");
        return state;
    }
    private static void Ack(InventoryAttemptState state, string operation = Operation)
        => Require(state.Receipt(7, "actor", operation, true, true), "Matching receipt was not accepted");
    private static void Confirmed(InventoryAttemptState state, string message)
        => Require(state.Snapshot?.Phase == InventoryAttemptPhase.Confirmed && !state.Pending && state.Snapshot.CommittedAck && state.Snapshot.FreshAcceptedRows, message);

    public static void Run()
    {
        var groups = 0;
        var state = Begin(); var captured = state.Snapshot!;
        Require(!state.Begin(7, "actor", InventoryAttemptKind.Move, Source, new(10), MoveTarget, EquipOperation, 1), "Pending lock allowed a second send");
        state.Tick(1.501);
        Require(state.Pending && ReferenceEquals(captured, state.Snapshot), "Cosmetic expiry changed pending attempt"); groups++;

        Require(!state.Receipt(7, "actor", Operation, false, true) && !state.Receipt(8, "actor", Operation, true, true) &&
            !state.Receipt(7, "other-actor", Operation, true, true) && !state.Receipt(7, "actor", EquipOperation, true, true), "Wrong caller/session/actor/operation settled attempt");
        state.Observe(Rows(Inventory(11), 8)); state.Observe(Rows(Inventory(11), actor: "other-actor"));
        Require(!state.Snapshot!.CommittedAck && !state.Snapshot.FreshAcceptedRows, "Wrong rows crossed actor/session"); groups++;

        Ack(state); Require(state.Pending && state.Snapshot!.Phase == InventoryAttemptPhase.AckWaitingRows, "ACK alone confirmed move");
        state.Observe(Rows(Inventory(11, x: 3))); state.Observe(Rows(Inventory(10))); state.Observe(Rows(Inventory(11), pinRevision: 4));
        state.Observe(Rows(Inventory(11), weaponRevision: 6)); state.Observe(Rows(Inventory(11, includeItem: false)));
        state.Observe(Rows(Inventory(11) with { Items = Inventory(11).Items.Select(item => item with { Definition = null }).ToArray() }));
        Require(state.Pending && !state.Snapshot!.FreshAcceptedRows, "Wrong/stale/missing/pin-changed target confirmed move");
        state.Observe(Rows(Inventory(11))); Confirmed(state, "ACK then intended fresh rows did not confirm");
        Require(!captured.CommittedAck && captured.Phase == InventoryAttemptPhase.Pending, "Previously captured immutable snapshot changed"); groups++;

        state = Begin(); state.Observe(Rows(Inventory(11)));
        Require(state.Pending && state.Snapshot!.FreshAcceptedRows && !state.Snapshot.CommittedAck, "Rows alone confirmed move");
        Ack(state); Confirmed(state, "Rows then ACK did not confirm"); groups++;

        state = Begin(); Ack(state); state.Tick(10);
        Require(state.Pending, "Deadline expired at equality"); state.Tick(10.001);
        Require(!state.Pending && state.Snapshot!.Phase == InventoryAttemptPhase.Uncertain, "Unconfirmed timeout was not uncertain");
        state.Observe(Rows(Inventory(11))); Confirmed(state, "Exact late rows could not settle known committed operation"); groups++;

        state = Begin(); state.Tick(10.001); var previous = state.Snapshot!;
        Require(state.Begin(7, "actor", InventoryAttemptKind.Move, Source, new(11), MoveTarget, EquipOperation, 11), "Explicit new attempt after uncertainty failed");
        Require(!state.Receipt(7, "actor", Operation, true, true) && !state.Snapshot!.CommittedAck && state.Snapshot.Generation > previous.Generation,
            "Old operation receipt confirmed newer explicit attempt"); groups++;

        state = Begin(); state.Observe(Rows(Inventory(11))); state.SessionChanged("Session lost");
        Require(!state.Receipt(7, "actor", Operation, true, true), "Retired-session ACK revived attempt");
        state.Observe(Rows(Inventory(12))); Require(state.Snapshot!.Phase == InventoryAttemptPhase.SessionChanged, "Retired rows revived attempt"); groups++;

        state = Begin(InventoryAttemptKind.Equip, new(EquipmentSlot: "hand", ItemId: "item", DestinationKind: InventoryDestinationKind.Equipment));
        Ack(state); state.Observe(Rows(Inventory(11, "", "helmet", 0, 0, false))); Require(state.Pending, "Wrong equipment slot confirmed equip");
        state.Observe(Rows(Inventory(11, "", "hand", 0, 0, false))); Confirmed(state, "Equip fresh accepted hand row failed"); groups++;

        state = Begin(InventoryAttemptKind.AssignHotbar, new(HotbarSlot: 0, ItemId: "item", DestinationKind: InventoryDestinationKind.Binding));
        Ack(state); state.Observe(Rows(Inventory(11, binding: "other"))); Require(state.Pending, "Wrong binding confirmed assign");
        state.Observe(Rows(Inventory(11))); Confirmed(state, "Exact binding did not confirm"); groups++;

        state = new(); Require(state.Begin(7, "actor", InventoryAttemptKind.AssignHotbar, null, new(10),
            new(HotbarSlot: 0, ItemId: "", DestinationKind: InventoryDestinationKind.Binding), Operation, 0), "Clear binding begin failed");
        Ack(state); state.Observe(Rows(Inventory(11, binding: "item"))); Require(state.Pending, "Nonempty binding confirmed clear");
        state.Observe(Rows(Inventory(11, includeItem: false, binding: ""))); Confirmed(state, "Explicit empty binding target did not confirm"); groups++;

        state = Begin(InventoryAttemptKind.Transfer, new(ItemId: "item", DestinationKind: InventoryDestinationKind.Carried));
        Ack(state); state.Observe(Rows(Inventory(11, "source", x: 0, y: 0, rotated: false))); Require(state.Pending, "Unrelated global revision confirmed untransferred item");
        state.Observe(Rows(Inventory(11))); Confirmed(state, "Server-selected carried placement did not confirm transfer"); groups++;

        state = Begin(InventoryAttemptKind.Drop, new(ItemId: "item", DestinationKind: InventoryDestinationKind.Ground));
        Ack(state); state.Observe(Rows(Inventory(11, "source", x: 0, y: 0, rotated: false), ground: true)); Require(state.Pending, "Unchanged noncarried source confirmed drop");
        state.Observe(Rows(Inventory(11, "ground", x: 0, y: 0, rotated: false))); Require(state.Pending, "Missing ground disclosure confirmed drop");
        state.Observe(Rows(Inventory(11, "ground", x: 0, y: 0, rotated: false), ground: true)); Confirmed(state, "Matching floor wrapper plus ground item did not confirm drop"); groups++;

        state = Begin(InventoryAttemptKind.Pickup, new(ItemId: "item", DestinationKind: InventoryDestinationKind.Carried));
        Ack(state); state.Observe(Rows(Inventory(11), ground: true)); Require(state.Pending, "Still disclosed ground item confirmed pickup");
        state.Observe(Rows(Inventory(11))); Confirmed(state, "Matching carried item and removed ground row failed pickup"); groups++;

        state = Begin(InventoryAttemptKind.Transfer, new(EquipmentSlot: "back", ItemId: "item", DestinationKind: InventoryDestinationKind.Carried));
        Ack(state); state.Observe(Rows(Inventory(11, "", "hand", 0, 0, false))); Require(state.Pending, "Default floor transfer accepted unrelated equipment slot");
        state.Observe(Rows(Inventory(11, "", "back", 0, 0, false))); Confirmed(state, "Source default floor-backpack autoequip path was not reconciled"); groups++;

        state = Begin(cargo: true); Ack(state);
        state.Observe(Rows(Inventory(10, itemRevision: 10))); state.Observe(Rows(Inventory(10, sourceRevision: 20)));
        state.Observe(Rows(Inventory(10, destinationRevision: 30))); state.Observe(Rows(Inventory(10, includeSource: false)));
        Require(state.Pending, "Unchanged item/source/destination scoped revision or lost source confirmed cargo");
        state.Observe(Rows(Inventory(10))); Confirmed(state, "Scoped item/source/destination rows could not confirm without unrelated character revision"); groups++;

        state = Begin(InventoryAttemptKind.Equip, new(EquipmentSlot: "hand", ItemId: "item", DestinationKind: InventoryDestinationKind.Equipment), cargo: true, composed: true);
        captured = state.Snapshot!; state.Observe(Rows(Inventory(11))); Require(!state.CargoTransferReady, "Cargo equip advanced without transfer ACK");
        Ack(state); Require(state.CargoTransferReady && state.Snapshot!.AcceptedStages == 1, "Transfer did not become ready");
        Require(!state.AdvanceCargoEquip(Operation, 11, 1), "Composed equip reused transfer operation UUID");
        Require(!state.AdvanceCargoEquip(EquipOperation, 10, 1), "Composed equip reused the pre-transfer inventory revision");
        Require(state.AdvanceCargoEquip(EquipOperation, 11, 1), "Composed equip did not advance");
        Require(state.Snapshot!.Generation == captured.Generation && state.Snapshot.OperationId == captured.OperationId &&
            state.Snapshot.StageOperationId == EquipOperation && state.Snapshot.StageExpected.InventoryRevision == 11 && state.Snapshot.AcceptedStages == 1,
            "Composed route changed logical token or stale expected revision");
        Require(!state.Receipt(7, "actor", Operation, true, true), "Late stage1 ACK confirmed stage2");
        state.Observe(Rows(Inventory(11, "", "hand", 0, 0, false))); Require(!state.Snapshot.FreshAcceptedRows, "Stage2 accepted stale character revision");
        Ack(state, EquipOperation); state.Observe(Rows(Inventory(12, "", "hand", 0, 0, false)));
        Confirmed(state, "Composed accepted equip did not confirm"); Require(state.Snapshot.AcceptedStages == 2, "Composed success lost accepted stage count"); groups++;

        state = Begin(InventoryAttemptKind.Equip, new(EquipmentSlot: "hand", ItemId: "item", DestinationKind: InventoryDestinationKind.Equipment), cargo: true, composed: true);
        Ack(state); state.Observe(Rows(Inventory(11))); Require(state.AdvanceCargoEquip(EquipOperation, 11, 1), "Partial failure setup failed");
        state.Receipt(7, "actor", EquipOperation, true, false, new string('x', 500) + "\nsecret-free bounded reason");
        Require(state.Snapshot!.Phase == InventoryAttemptPhase.Rejected && state.Snapshot.AcceptedStages == 1 && !state.Pending && state.Snapshot.Reason.Length <= 300,
            "Stage2 failure lost partial committed accounting or bounded reason");
        Require(state.Snapshot.Reason.Contains("cargo transfer already completed", StringComparison.Ordinal) && !state.Snapshot.Reason.Contains('\n'),
            "Bounded rejection lost the factual partial success explanation");
        Require(state.Snapshot.AcceptedPlacement?.ContainerId == "destination", "Stage2 failure erased accepted carried placement"); groups++;

        state = Begin(InventoryAttemptKind.Equip, new(EquipmentSlot: "hand", ItemId: "item", DestinationKind: InventoryDestinationKind.Equipment), cargo: true, composed: true);
        state.Tick(10.001); Ack(state); state.Observe(Rows(Inventory(11)));
        Require(!state.CargoTransferReady && !state.AdvanceCargoEquip(EquipOperation, 11, 12) && state.Snapshot!.Phase == InventoryAttemptPhase.Uncertain && state.Snapshot.AcceptedStages == 1,
            "Late uncertain transfer automatically dispatched equip"); groups++;

        state = Begin(cargo: true, composed: true); Ack(state); state.Observe(Rows(Inventory(11)));
        state.SessionChanged("Context lost"); Require(!state.AdvanceCargoEquip(EquipOperation, 11, 1) && state.Snapshot!.AcceptedStages == 1,
            "Lost session advanced composition or erased accepted transfer"); groups++;

        state = new(); var mutableSources = new[] { Source };
        Require(state.Begin(7, "actor", InventoryAttemptKind.TakeAll, null, new(10), new(DestinationKind: InventoryDestinationKind.Carried), Operation, 0, sources: mutableSources), "Bulk begin failed");
        mutableSources[0] = Source with { ItemId = "different" }; Ack(state); state.Observe(Rows(Inventory(11)));
        Confirmed(state, "Bulk target did not match intended moved source item"); Require(state.Snapshot!.Sources[0].ItemId == "item", "Attempt did not copy immutable bulk sources"); groups++;

        Console.WriteLine($"Inventory attempt {groups} adversarial groups passed: caller/session/op/pin/target correlation, row/ACK order, cosmetic/real deadlines, composed cargo partial outcomes.");
    }
}
