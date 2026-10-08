using System;
using System.Collections.Generic;
using System.Linq;

namespace Sidereal.Native;

/// <summary>A proposal made only from currently disclosed storage. The reducer
/// independently validates reach, containment, mass and every expected revision.</summary>
public sealed record InventoryCargoPlan(string ItemId, ulong ExpectedItemRevision,
    string SourceContainerId, ulong ExpectedSourceRevision, string DestinationContainerId,
    ulong ExpectedDestinationRevision, ulong ExpectedCharacterRevision, int X, int Y, bool Rotated)
{
    public static InventoryCargoPlan Create(InventorySnapshot snapshot, string itemId, string destinationId,
        (int X, int Y, bool Rotated)? exact = null)
    {
        if (!snapshot.Available) throw new InvalidOperationException("Inventory is unavailable. Reconnect before transferring.");
        var item = snapshot.Item(itemId) ?? throw new InvalidOperationException("Item is no longer available.");
        if (item.ContainerId.Length == 0) throw new InvalidOperationException("Stow the equipped item before moving it to storage.");
        var source = snapshot.Container(item.ContainerId) ?? throw new InvalidOperationException("Source storage access changed.");
        var candidates = destinationId.Length != 0
            ? new[] { snapshot.Container(destinationId) ?? throw new InvalidOperationException("Destination storage access changed.") }
            : snapshot.Containers.Where(c => c.Carried && c.Kind == "grid" && c.Id != source.Id)
                .OrderBy(c => c.ParentItemId.Length == 0).ToArray();
        // Follow the disclosed tree so a missing parent, cycle, or self-containment
        // cannot be hidden by a plausible destination rectangle.
        string Root(InventoryContainerView container)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                if (!seen.Add(container.Id)) throw new InvalidOperationException("Invalid storage containment cycle.");
                if (container.ParentItemId == item.Id) throw new InvalidOperationException("An item cannot contain itself.");
                if (container.ParentItemId.Length == 0) return container.Id;
                var parent = snapshot.Item(container.ParentItemId) ?? throw new InvalidOperationException("Parent storage item is no longer available.");
                if (parent.EquipmentSlot.Length != 0) return container.Id;
                container = snapshot.Container(parent.ContainerId) ?? throw new InvalidOperationException("Parent storage access changed.");
            }
        }
        _ = Root(source);
        foreach (var destination in candidates)
        {
            _ = Root(destination);
            var placement = exact ?? snapshot.FirstPlacement(itemId, destination.Id);
            if (placement == null || !snapshot.Fits(itemId, destination.Id, placement.Value.X, placement.Value.Y, placement.Value.Rotated)) continue;
            if (item.ScopedRevision == null || source.ScopedRevision == null || destination.ScopedRevision == null)
                throw new InvalidOperationException("Storage revisions are unavailable. Reopen storage and try again.");
            return new(item.Id, item.ScopedRevision.Value, source.Id, source.ScopedRevision.Value,
                destination.Id, destination.ScopedRevision.Value, snapshot.Revision,
                placement.Value.X, placement.Value.Y, placement.Value.Rotated);
        }
        throw new InvalidOperationException("No compatible storage space is available.");
    }
}
