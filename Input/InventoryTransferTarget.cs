using System;
using System.Collections.Generic;
using System.Linq;

namespace Sidereal.Native.Input;

/// <summary>Structural and spatial advice for an explicit disclosed destination.
/// Complete payload/carry validation and accepted placement belong to the reducer.</summary>
public sealed record InventoryTransferEligibility(bool Ready, string Reason, string ContainerId,
    (int X, int Y, bool Rotated)? SpatialPlacement = null);

public static class InventoryTransferTarget
{
    public static bool IsPersonalContainer(InventorySnapshot snapshot, InventoryContainerView container) =>
        snapshot.Available && container.Kind == "grid" && !IsGround(container) &&
        (container.ParentItemId.Length == 0 ? container.Carried :
            snapshot.Item(container.ParentItemId)?.EquipmentSlot.Length > 0);

    public static string PersonalLabel(InventorySnapshot snapshot, InventoryContainerView container) =>
        container.ParentItemId.Length == 0 ? "Pockets" :
        snapshot.Item(container.ParentItemId)?.EquipmentSlot == "back" ? "Backpack" : container.Name;

    private static bool IsGround(InventoryContainerView container) =>
        container.PlacementId.StartsWith("ground:", StringComparison.Ordinal);
    private static bool KnownFootprint(InventoryItemView item) =>
        item.Definition is { Width: > 0, Height: > 0, Revision: > 0 } definition && definition.Id == item.DefinitionId;

    public static InventoryTransferEligibility Evaluate(InventorySnapshot snapshot, string itemId, string destinationId)
    {
        InventoryTransferEligibility Block(string reason) => new(false, reason, destinationId);
        if (!snapshot.Available) return Block("Inventory is unavailable.");
        var item = snapshot.Item(itemId);
        if (item == null) return Block("This item is no longer disclosed.");
        if (!KnownFootprint(item))
            return Block("The pinned footprint is unavailable.");
        var destination = snapshot.Container(destinationId);
        if (destination == null) return Block("Storage access changed. Choose current storage.");
        if (IsGround(destination)) return Block("Ground items are pickup sources, not receiving storage.");
        if (destination.Kind != "grid") return Block("This storage does not accept item placement.");
        if (destination.Id == item.ContainerId) return Block("This item is already in this storage.");
        var source = snapshot.Container(item.ContainerId);
        if (item.ContainerId.Length > 0 && source == null) return Block("Source storage access changed.");
        if (item.ContainerId.Length == 0 && item.EquipmentSlot.Length == 0) return Block("The accepted item location is unavailable.");

        string? Ancestry(InventoryContainerView container)
        {
            var seenContainers = new HashSet<string>(StringComparer.Ordinal);
            var seenItems = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                if (!seenContainers.Add(container.Id)) return "Invalid storage containment cycle.";
                if (container.ParentItemId.Length == 0) return null;
                if (container.ParentItemId == item.Id) return "An item cannot contain itself.";
                if (!seenItems.Add(container.ParentItemId)) return "Invalid storage containment cycle.";
                var parent = snapshot.Item(container.ParentItemId);
                if (parent == null) return "Parent storage item is no longer disclosed.";
                if (parent.EquipmentSlot.Length > 0) return null;
                if (snapshot.Container(parent.ContainerId) is not {} ancestor) return "Parent storage access changed.";
                container = ancestor;
            }
        }
        if (source != null && Ancestry(source) is {} sourceError) return Block(sourceError);
        if (Ancestry(destination) is {} destinationError) return Block(destinationError);
        if (snapshot.UsesScopedCargo(itemId, destinationId))
        {
            if (item.EquipmentSlot.Length > 0 || source == null) return Block("Stow in carried storage first.");
            if (item.ScopedRevision == null || source.ScopedRevision == null || destination.ScopedRevision == null)
                return Block("Storage revisions are unavailable. Reopen storage.");
        }
        if (snapshot.Items.Any(other => other.ContainerId == destinationId && other.Id != itemId && !KnownFootprint(other)))
            return Block("An occupied pinned footprint is unavailable.");
        // This intentionally follows accepted rotation/inverse, never the local held R.
        var placement = snapshot.FirstPlacement(itemId, destinationId);
        return placement == null ? Block("No compatible spatial room in this storage.") :
            new(true, "Placement and capacity require server confirmation.", destinationId, placement);
    }
}
