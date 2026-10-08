using System;
using Godot;
using Sidereal.Native;
using Sidereal.InventoryUi;

namespace Sidereal.Ui;

/// <summary>A floating, fixed container view over currently actor-disclosed storage.</summary>
public partial class StorageWindow : DockWindow
{
    private readonly ClientCore core;
    private readonly ContainerPane pane;
    private readonly bool demo;
    private readonly Label detail;
    private string selected="";
    public string ContainerId { get; }
    public event Action<string>? ItemInspectRequested, ContainerOpenRequested;
    public event Action? AccessLost;
    public StorageWindow(ClientCore core,bool demo,InventoryContainerView container,Vector2 position)
        :base("storage-"+container.Id,container.Name,position,new Vector2(410,590))
    {
        this.core=core;this.demo=demo;ContainerId=container.Id;
        pane=new ContainerPane(core,demo,0,container.Id);Content.AddChild(pane);
        detail=UiKit.Paragraph("Select an item to inspect its pinned definition.",0).Sized(13);Content.AddChild(detail);
        pane.Selected+=id=>{selected=id;ItemInspectRequested?.Invoke(id);};
        pane.ContainerOpenRequested+=id=>ContainerOpenRequested?.Invoke(id);
    }
    public void Refresh()
    {
        var snapshot=InventoryPresentation.Read(core,demo);
        if(snapshot.Container(ContainerId)==null)
        {
            Hide();
            if(ItemDrag.Owner?.Core==core)ItemDrag.Owner.CancelLocal();
            GetViewport().GuiReleaseFocus();core.ReleaseControls();AccessLost?.Invoke();return;
        }
        pane.Refresh(snapshot);
        detail.Text=snapshot.Item(selected) is {} item?ItemPresentation.Tooltip(core,item):"Select an item to inspect its pinned definition.";
    }
}
