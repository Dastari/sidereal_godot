using System;
using System.Collections.Generic;
using System.Linq;

namespace Sidereal.Native.Input;

public enum InventoryAttemptKind { Move, Equip, AssignHotbar, ActivateHotbar, Transfer, Drop, TakeAll, StoreAll, Pickup, ClaimArmory }
public enum InventoryAttemptPhase { Pending, AckWaitingRows, Confirmed, Rejected, Uncertain, SessionChanged }
public enum InventoryAttemptStage { Single, CargoTransfer, CargoEquip }
public enum InventoryDestinationKind { Exact, Carried, Ground, Equipment, Binding, Revision }

public sealed record InventoryAttemptSource(string ItemId, string DefinitionId, ulong ItemRevision,
    string WeaponDefinitionId, ulong WeaponRevision, string ContainerId, string EquipmentSlot,
    int X, int Y, bool Rotated, string PlacementId = "", string InstanceId = "", string DeckId = "");
public sealed record InventoryAttemptRevisions(ulong InventoryRevision, ulong? ItemRevision = null,
    ulong? SourceRevision = null, ulong? DestinationRevision = null, bool ScopedRevisionRequired = false);
public sealed record InventoryAttemptTarget(string ContainerId = "", int? X = null, int? Y = null,
    bool? Rotated = null, string EquipmentSlot = "", byte? HotbarSlot = null, string ItemId = "",
    InventoryDestinationKind DestinationKind = InventoryDestinationKind.Exact);
public sealed record InventoryAcceptedPlacement(string ItemId, string ContainerId, string EquipmentSlot, int X, int Y, bool Rotated);
public sealed record InventoryAttemptSnapshot(ulong Generation, string OperationId, string StageOperationId,
    ulong SessionGeneration, string ActorId, InventoryAttemptKind Kind, InventoryAttemptSource? Source,
    InventoryAttemptRevisions Expected, InventoryAttemptTarget Target, InventoryAttemptPhase Phase,
    InventoryAttemptStage Stage, bool CommittedAck, bool FreshAcceptedRows, int AcceptedStages, string Reason,
    InventoryAttemptRevisions StageExpected, InventoryAttemptTarget StageTarget,
    IReadOnlyList<InventoryAttemptSource> Sources, InventoryAcceptedPlacement? AcceptedPlacement = null,
    InventoryAttemptTarget? TransferTarget = null)
{
    public bool IsPending => Phase is InventoryAttemptPhase.Pending or InventoryAttemptPhase.AckWaitingRows;
}
public sealed record InventoryAttemptRows(ulong SessionGeneration, string ActorId, InventorySnapshot Inventory,
    IReadOnlyDictionary<string, (string DefinitionId, ulong ItemRevision, ulong WeaponRevision)> Pins,
    IReadOnlySet<string> GroundItems);

/// <summary>One bounded current/latest attempt. A reducer receipt and fresh intended rows
/// are independent evidence; neither cosmetic time nor gameplay input epochs settle it.</summary>
public sealed class InventoryAttemptState
{
    public InventoryAttemptSnapshot? Snapshot { get; private set; }
    private ulong generation;
    private double deadline;
    public bool Pending => Snapshot?.IsPending == true;
    public bool CargoTransferReady => Snapshot is { Stage: InventoryAttemptStage.CargoTransfer, CommittedAck: true, FreshAcceptedRows: true, AcceptedStages: 1, IsPending: true };

    public bool Begin(ulong session, string actor, InventoryAttemptKind kind, InventoryAttemptSource? source,
        InventoryAttemptRevisions expected, InventoryAttemptTarget target, string operation, double now,
        InventoryAttemptStage stage = InventoryAttemptStage.Single, InventoryAttemptTarget? stageTarget = null,
        IEnumerable<InventoryAttemptSource>? sources = null)
    {
        if (Pending || session == 0 || actor.Length == 0 || !double.IsFinite(now) || !Guid.TryParseExact(operation, "D", out _)) return false;
        var captured = Array.AsReadOnly((sources ?? (source == null ? Array.Empty<InventoryAttemptSource>() : new[] { source })).ToArray());
        Snapshot = new(++generation, operation, operation, session, actor, kind, source, expected, target,
            InventoryAttemptPhase.Pending, stage, false, false, 0, "Waiting for server confirmation…", expected,
            stageTarget ?? target, captured, TransferTarget: stage == InventoryAttemptStage.CargoTransfer ? stageTarget : null);
        deadline = now + 10;
        return true;
    }

    public bool Receipt(ulong session, string actor, string operation, bool caller, bool committed, string reason = "")
    {
        if (!caller || Snapshot is not { } attempt || session != attempt.SessionGeneration || actor != attempt.ActorId ||
            operation != attempt.StageOperationId || attempt.Phase is InventoryAttemptPhase.Confirmed or InventoryAttemptPhase.Rejected or InventoryAttemptPhase.SessionChanged) return false;
        if (!committed)
        {
            Snapshot = attempt with { Phase = InventoryAttemptPhase.Rejected, Reason = Bounded("Rejected: " + Bounded(reason)[..Math.Min(reason.Length, 180)] + Partial(attempt)) };
            return true;
        }
        Snapshot = attempt with { CommittedAck = true,
            Phase = attempt.Phase == InventoryAttemptPhase.Uncertain ? InventoryAttemptPhase.Uncertain : InventoryAttemptPhase.AckWaitingRows,
            Reason = "Server accepted the request. Waiting for the intended inventory rows…" };
        CompleteIfReady();
        return true;
    }

    public void Observe(InventoryAttemptRows rows)
    {
        if (Snapshot is not { } attempt || attempt.Phase is InventoryAttemptPhase.Confirmed or InventoryAttemptPhase.Rejected or InventoryAttemptPhase.SessionChanged ||
            rows.SessionGeneration != attempt.SessionGeneration || rows.ActorId != attempt.ActorId || !rows.Inventory.Available || attempt.FreshAcceptedRows) return;
        if (!Matches(attempt, rows, out var accepted)) return;
        Snapshot = attempt with { FreshAcceptedRows = true, AcceptedPlacement = accepted ?? attempt.AcceptedPlacement };
        CompleteIfReady();
    }

    private void CompleteIfReady()
    {
        if (Snapshot is not { CommittedAck: true, FreshAcceptedRows: true } attempt) return;
        if (attempt.Stage == InventoryAttemptStage.CargoTransfer)
            Snapshot = attempt with { AcceptedStages = 1, Reason = attempt.IsPending
                ? "Cargo transfer confirmed. Preparing equip from accepted carried inventory…"
                : "Cargo transfer confirmed after the waiting period. Choose the carried item explicitly to equip it." };
        else Snapshot = attempt with { Phase = InventoryAttemptPhase.Confirmed, AcceptedStages = attempt.Stage == InventoryAttemptStage.CargoEquip ? 2 : 1,
            Reason = "Inventory confirmed by the server." };
    }

    public bool AdvanceCargoEquip(string operation, ulong inventoryRevision, double now)
        => AdvanceCargoEquip(operation, new InventoryAttemptRevisions(inventoryRevision), now);
    public bool AdvanceCargoEquip(string operation, InventoryAttemptRevisions expected, double now)
    {
        if (!CargoTransferReady || Snapshot is not { } attempt || operation == attempt.OperationId || !Guid.TryParseExact(operation, "D", out _) ||
            expected.InventoryRevision <= attempt.Expected.InventoryRevision || expected.ScopedRevisionRequired || !double.IsFinite(now)) return false;
        Snapshot = attempt with { Stage = InventoryAttemptStage.CargoEquip, StageOperationId = operation,
            StageExpected = expected, StageTarget = attempt.Target, Phase = InventoryAttemptPhase.Pending,
            CommittedAck = false, FreshAcceptedRows = false, Reason = "Cargo transfer confirmed. Waiting for equip confirmation…" };
        deadline = now + 10;
        return true;
    }

    public void Tick(double now)
    {
        if (Snapshot is { IsPending: true } attempt && now > deadline)
            Snapshot = attempt with { Phase = InventoryAttemptPhase.Uncertain,
                Reason = Bounded("Confirmation is delayed. Review the accepted inventory before explicitly retrying." + Partial(attempt)) };
    }
    public void Uncertain(string reason)
    {
        if (Snapshot is { IsPending: true } attempt)
            Snapshot = attempt with { Phase = InventoryAttemptPhase.Uncertain, Reason = Bounded(reason + Partial(attempt)) };
    }
    public void SessionChanged(string reason)
    {
        if (Snapshot is { } attempt && attempt.Phase is not (InventoryAttemptPhase.Confirmed or InventoryAttemptPhase.Rejected or InventoryAttemptPhase.SessionChanged))
            Snapshot = attempt with { Phase = InventoryAttemptPhase.SessionChanged, Reason = Bounded(reason + Partial(attempt)) };
    }
    private static string Partial(InventoryAttemptSnapshot attempt) => attempt.AcceptedStages > 0 && attempt.Stage != InventoryAttemptStage.Single
        ? " The cargo transfer already completed; the item remains where the server placed it." : "";
    private static string Bounded(string reason) => reason.Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(reason.Length, 300)];

    public static bool PinMatches(InventoryAttemptSource source, InventoryAttemptRows rows)
    {
        var item = rows.Inventory.Item(source.ItemId);
        if (item == null || item.DefinitionId != source.DefinitionId || item.Definition?.Revision != source.ItemRevision) return false;
        // Revision-one fallback is a presentation contract only when no pin exists.
        return rows.Pins.TryGetValue(source.ItemId, out var pin)
            ? pin.DefinitionId == source.DefinitionId && pin.ItemRevision == source.ItemRevision && pin.WeaponRevision == source.WeaponRevision
            : source.ItemRevision == 1 && source.WeaponRevision == 0;
    }

    public static bool Matches(InventoryAttemptSnapshot attempt, InventoryAttemptRows rows, out InventoryAcceptedPlacement? accepted)
    {
        accepted = null;
        var inventory = rows.Inventory; var expected = attempt.StageExpected; var target = attempt.StageTarget;
        if (rows.SessionGeneration != attempt.SessionGeneration || rows.ActorId != attempt.ActorId || !inventory.Available) return false;
        if (!expected.ScopedRevisionRequired && inventory.Revision <= expected.InventoryRevision) return false;
        if (target.DestinationKind == InventoryDestinationKind.Binding)
        {
            if (target.HotbarSlot is not { } slot || !inventory.Hotbar.TryGetValue(slot, out var binding) || binding != target.ItemId) return false;
            return attempt.Source == null || PinMatches(attempt.Source, rows);
        }
        if (target.DestinationKind == InventoryDestinationKind.Revision) return inventory.Revision > expected.InventoryRevision;
        foreach (var source in attempt.Sources)
        {
            if (!PinMatches(source, rows) || inventory.Item(source.ItemId) is not { } item) continue;
            if (expected.ScopedRevisionRequired && (expected.ItemRevision is not { } itemRevision || !(item.ScopedRevision > itemRevision) ||
                expected.SourceRevision is not { } sourceRevision || !(inventory.Container(source.ContainerId)?.ScopedRevision > sourceRevision) ||
                expected.DestinationRevision is not { } destinationRevision || !(inventory.Container(target.ContainerId)?.ScopedRevision > destinationRevision))) continue;
            bool match = target.DestinationKind switch
            {
                InventoryDestinationKind.Exact => item.ContainerId == target.ContainerId && item.EquipmentSlot.Length == 0 &&
                    (target.X == null || item.X == target.X) && (target.Y == null || item.Y == target.Y) && (target.Rotated == null || item.Rotated == target.Rotated),
                InventoryDestinationKind.Equipment => item.ContainerId.Length == 0 && item.EquipmentSlot == target.EquipmentSlot && item.X == 0 && item.Y == 0 && !item.Rotated,
                InventoryDestinationKind.Carried => item.ContainerId != source.ContainerId &&
                    (item.EquipmentSlot.Length == 0 && inventory.Container(item.ContainerId)?.Carried == true ||
                        target.EquipmentSlot == "back" && item.ContainerId.Length == 0 && item.EquipmentSlot == "back") &&
                    (attempt.Kind != InventoryAttemptKind.Pickup || !rows.GroundItems.Contains(item.Id)),
                InventoryDestinationKind.Ground => item.ContainerId != source.ContainerId && item.EquipmentSlot.Length == 0 && item.X == 0 && item.Y == 0 && !item.Rotated &&
                    inventory.Container(item.ContainerId)?.Carried == false && rows.GroundItems.Contains(item.Id),
                _ => false
            };
            if (!match) continue;
            accepted = new(item.Id, item.ContainerId, item.EquipmentSlot, item.X, item.Y, item.Rotated);
            return true;
        }
        return false;
    }
}
