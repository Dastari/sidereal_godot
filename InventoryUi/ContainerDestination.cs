using System;
using Godot;
using Sidereal.Native;
using Sidereal.Native.Input;
using Sidereal.Ui;

namespace Sidereal.InventoryUi;

/// <summary>Only these registered content controls opt into held-item button routing.</summary>
internal partial class ContainerDestination : Button, IInventoryInteractionSurface
{
    private readonly ClientCore core;
    private readonly bool demo;
    private readonly Func<string> containerId;
    internal InventoryTargetSurfaceRole Role { get; }
    ClientCore IInventoryInteractionSurface.InventoryCore => core;
    bool IInventoryInteractionSurface.InventoryDemo => demo;

    internal ContainerDestination(ClientCore core, bool demo, Func<string> containerId,
        InventoryTargetSurfaceRole role, Action<string>? activate = null)
    {
        this.core = core; this.demo = demo; this.containerId = containerId; Role = role;
        Name = role.ToString(); ClipText = true; AutowrapMode = TextServer.AutowrapMode.Off;
        CustomMinimumSize = new Vector2(role == InventoryTargetSurfaceRole.ContainerTab ? 106 : 0, 44);
        SizeFlagsHorizontal = role == InventoryTargetSurfaceRole.ContainerTab ? SizeFlags.ShrinkBegin : SizeFlags.ExpandFill;
        ToggleMode = role == InventoryTargetSurfaceRole.ContainerTab;
        FocusMode = ToggleMode ? FocusModeEnum.All : FocusModeEnum.None;
        MouseFilter = ToggleMode ? MouseFilterEnum.Stop : MouseFilterEnum.Pass;
        Alignment = ToggleMode ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        AddThemeFontSizeOverride("font_size", ToggleMode ? 13 : 12);
        if (activate != null) Pressed += () =>
        {
            if (!ItemDrag.InteractionEnabled(core) || ItemDrag.Owner?.Core == core && ItemDrag.Owner.Capturing) return;
            var current = InventoryPresentation.Read(core, demo);
            if (current.Container(containerId()) is {} c && InventoryTransferTarget.IsPersonalContainer(current, c)) activate(c.Id);
        };
    }

    internal void Refresh(InventorySnapshot snapshot, bool selected = false)
    {
        var container = snapshot.Container(containerId());
        Visible = container is { Kind: "grid" };
        if (container == null) return;
        Text = Role == InventoryTargetSurfaceRole.ContainerTab ? InventoryTransferTarget.PersonalLabel(snapshot, container) :
            $"{container.Name}\n{container.Width} × {container.Height} cells · Payload limit {container.MaxMassKg:0.##} kg";
        if (ToggleMode) SetPressedNoSignal(selected);
        TooltipText = ItemDrag.TooltipsAllowed(this, core) ?
            $"{container.Name}\nTransfer here: placement and capacity require server confirmation." : "";
    }

    InventorySourceHit? IInventoryInteractionSurface.InventorySource(Vector2 local) => null;
    InventoryTarget? IInventoryInteractionSurface.InventoryTarget(InventoryItemView item, bool rotated, Vector2 fraction, Vector2 local)
    {
        var rect = new Rect2(Vector2.Zero, Size);
        if (!rect.HasPoint(local)) return null;
        var snapshot = InventoryPresentation.Read(core, demo);
        var id = containerId();
        var result = InventoryTransferTarget.Evaluate(snapshot, item.Id, id);
        if (Role == InventoryTargetSurfaceRole.ContainerTab && snapshot.Container(id) is {} c &&
            !InventoryTransferTarget.IsPersonalContainer(snapshot, c))
            result = new(false, "This personal storage is no longer disclosed here.", id);
        return new(this, rect, InventoryTargetKind.Transfer, result.Ready, result.Ready ? "" : result.Reason,
            id, Priority: 60, SurfaceRole: Role,
            ActionLabel: $"Transfer to {snapshot.Container(id)?.Name ?? "storage"} · Server checks placement and capacity");
    }

    internal object SmokeGeometry()
    {
        var rect = GetGlobalRect();
        return new
        {
            role = Role.ToString(), container = containerId(), Text, selected = ToggleMode && ButtonPressed,
            x = rect.Position.X, y = rect.Position.Y, width = rect.Size.X, height = rect.Size.Y,
            logicalWidth = Size.X, logicalHeight = Size.Y
        };
    }
}
