using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Sidereal.Native;
using Sidereal.Native.Input;
using Sidereal.Ui;
using NVector=System.Numerics.Vector2;

namespace Sidereal.InventoryUi;

internal sealed record InventorySourceHit(Control Owner,InventoryItemView Item,Rect2 Rect,Action? Select=null);
internal enum InventoryTargetKind { Cancel,Grid,Equip,Transfer,Quick,Hotbar,Blocked }
internal enum InventoryTargetSurfaceRole { Default,ContainerHeader,ContainerTab }
internal sealed record InventoryTarget(Control Owner,Rect2 Rect,InventoryTargetKind Kind,bool Valid,string Reason="",string ContainerId="",int X=0,int Y=0,bool Rotated=false,byte Slot=0,Action<string>? Bind=null,int Priority=0,InventoryTargetSurfaceRole SurfaceRole=InventoryTargetSurfaceRole.Default,string ActionLabel="")
{
    public bool Holds=>Kind is InventoryTargetKind.Grid or InventoryTargetKind.Equip;
    public bool NamedDestination=>SurfaceRole is InventoryTargetSurfaceRole.ContainerHeader or InventoryTargetSurfaceRole.ContainerTab;
}
internal interface IInventoryInteractionSurface
{
    ClientCore InventoryCore {get;}
    bool InventoryDemo {get;}
    InventorySourceHit? InventorySource(Vector2 local);
    InventorySourceHit? InventoryOrigin(string id)=>null;
    InventoryTarget? InventoryTarget(InventoryItemView item,bool rotated,Vector2 fraction,Vector2 local);
}

/// <summary>Godot adapter for one inventory-only pointer owner; accepted state remains in Core.</summary>
public partial class InventoryInteractionView : Control
{
    private readonly ClientCore core;
    private readonly bool demo;
    public InventoryInteraction State {get;}=new();
    public Func<bool>? Enabled {get;set;}
    public Func<Vector2,Control?>? TopWindowAt {get;set;}
    public Func<string?>? OpenTransferDestination {get;set;}
    private InventoryItemView? paintedItem;
    private InventoryTarget? candidate;
    private bool ordinaryPress,paintWasActive;
    private double Now=>Time.GetTicksMsec()/1000d;
    private bool Reduced=>NativePreferences.Current.Snapshot.ReducedMotion;
    private InventorySnapshot Snapshot=>InventoryPresentation.Read(core,demo);
    public bool Capturing=>State.Capturing;
    public bool Animating=>State.Animating(Now);
    public ClientCore Core=>core;
    internal InventoryItemView? ActiveItem=>State.Source!=null?paintedItem:null;
    public InventoryInteractionView(ClientCore core,bool demo)
    {
        this.core=core;this.demo=demo;Name="InventoryInteraction";MouseFilter=MouseFilterEnum.Ignore;ZAsRelative=false;ZIndex=4000;
    }
    public override void _Ready(){ItemDrag.Owner=this;GetWindow().FocusExited+=OnBlur;}
    public override void _ExitTree(){GetWindow().FocusExited-=OnBlur;CancelLocal();if(ItemDrag.Owner==this)ItemDrag.Owner=null;}
    private void OnBlur()=>CancelLocal();
    private Vector2 Pointer=>GetGlobalTransformWithCanvas().AffineInverse()*GetViewport().GetMousePosition();
    private static NVector N(Vector2 p)=>new(p.X,p.Y);
    private static InventoryPointerRect R(Rect2 r)=>new(r.Position.X,r.Position.Y,r.Size.X,r.Size.Y);
    private static Rect2 R(InventoryPointerRect r)=>new(r.X,r.Y,r.Width,r.Height);
    private static DockWindow? Frame(Control c){for(Node? n=c;n!=null;n=n.GetParent())if(n is DockWindow w)return w;return null;}
    private IEnumerable<Control> Surfaces(Node node)
    {
        foreach(var child in node.GetChildren())
        { if(child==this)continue;if(child is Control c&&c.IsVisibleInTree()&&c is IInventoryInteractionSurface surface&&surface.InventoryCore==core&&surface.InventoryDemo==demo)yield return c;foreach(var nested in Surfaces(child))yield return nested; }
    }
    private static IEnumerable<Control> OrdinaryControls(Node node)
    {
        foreach(var child in node.GetChildren())
        {if(child is Control c&&c.IsVisibleInTree()&&c is (BaseButton or LineEdit or TextEdit or ScrollBar))yield return c;foreach(var nested in OrdinaryControls(child))yield return nested;}
    }
    private static bool ModalVisible(Node node)
    {
        foreach(var child in node.GetChildren(true))
            if(child is Window window&&InventoryInteraction.BlocksPopup(window.Visible,window is PopupPanel,window.Exclusive,
                window.GetFlag(Window.Flags.NoFocus),window.GetFlag(Window.Flags.MousePassthrough))||ModalVisible(child))return true;
        return false;
    }
    private static void CancelPassiveTooltips(Node node,HashSet<Viewport>? cancelled=null)
    {
        cancelled??=new();
        foreach(var child in node.GetChildren())
        {
            if(child is Window {Visible:true} window&&!window.IsQueuedForDeletion()&&!InventoryInteraction.BlocksPopup(true,window is PopupPanel,window.Exclusive,
                window.GetFlag(Window.Flags.NoFocus),window.GetFlag(Window.Flags.MousePassthrough)))
            {
                // Godot owns the cached tooltip and its timer. Hiding or freeing its
                // PopupPanel bypasses that ownership (and can touch an already queued popup).
                // 4.7.2 has no bound cancel_tooltip API; the public hover boundary
                // invokes the owner's cancellation without input or keyboard/press focus changes.
                var viewport=window.GetParent().GetViewport();
                if(cancelled.Add(viewport)&&viewport.GuiGetHoveredControl()!=null)
                {viewport.NotifyMouseExited();viewport.NotifyMouseEntered();}
            }
            CancelPassiveTooltips(child,cancelled);
        }
    }
    internal Rect2 InView(Control control,Rect2 local)
    {
        var transform=GetGlobalTransformWithCanvas().AffineInverse()*control.GetGlobalTransformWithCanvas();
        var points=new[]{transform*local.Position,transform*local.End,transform*new Vector2(local.End.X,local.Position.Y),transform*new Vector2(local.Position.X,local.End.Y)};
        var lo=new Vector2(points.Min(p=>p.X),points.Min(p=>p.Y));var hi=new Vector2(points.Max(p=>p.X),points.Max(p=>p.Y));return new(lo,hi-lo);
    }
    private bool Usable(Control c,Vector2 pointer,Rect2 local)
    {
        if(!c.IsVisibleInTree())return false;
        var physical=GetGlobalTransformWithCanvas()*pointer;
        if(!c.GetViewportRect().HasPoint(physical)||!local.HasPoint(c.GetGlobalTransformWithCanvas().AffineInverse()*physical))return false;
        for(Node? n=c.GetParent();n is Control parent;n=n.GetParent())if((parent.ClipContents||parent is ScrollContainer)&&!parent.GetGlobalRect().HasPoint(physical))return false;
        var top=TopWindowAt?.Invoke(physical);return top==null?Frame(c)==null:top==Frame(c);
    }
    private InventorySourceHit? SourceAt(Vector2 pointer)
    {
        var physical=GetGlobalTransformWithCanvas()*pointer;
        foreach(var c in Surfaces(GetParent()))
        {
            var hit=((IInventoryInteractionSurface)c).InventorySource(c.GetGlobalTransformWithCanvas().AffineInverse()*physical);
            if(hit!=null&&Usable(c,pointer,hit.Rect))return hit;
        }
        return null;
    }
    private bool OrdinaryControl(Vector2 pointer)
        =>OrdinaryControls(GetParent()).Any(c=>Usable(c,pointer,new Rect2(Vector2.Zero,c.Size)));
    private bool WindowChrome(Vector2 pointer)
    {
        var physical=GetGlobalTransformWithCanvas()*pointer;
        if(TopWindowAt?.Invoke(physical) is not DockWindow frame)return false;
        var p=frame.GetGlobalTransformWithCanvas().AffineInverse()*physical;
        return p.Y<49||p.X<8||p.X>frame.Size.X-8||p.Y>frame.Size.Y-8||(p.X>frame.Size.X-26&&p.Y>frame.Size.Y-26);
    }
    private Rect2 VisibleRect(Control control)
    {
        var clip=InView(control,new Rect2(Vector2.Zero,control.Size));
        var inverse=GetGlobalTransformWithCanvas().AffineInverse();
        var viewport=GetViewport().GetVisibleRect();
        clip=clip.Intersection(new Rect2(inverse*viewport.Position,inverse.BasisXform(viewport.Size)));
        for(Node? n=control.GetParent();n is Control parent;n=n.GetParent())
            if(parent.ClipContents||parent is ScrollContainer)clip=clip.Intersection(InView(parent,new Rect2(Vector2.Zero,parent.Size)));
        return clip;
    }
    private Rect2? CurrentOrigin()
    {
        if(State.Source is not {} source)return null;
        foreach(var c in Surfaces(GetParent()))
        {
            if(c.GetPath().ToString()!=source.SurfaceId||((IInventoryInteractionSurface)c).InventoryOrigin(source.ItemId) is not {} hit)continue;
            var rect=InView(hit.Owner,hit.Rect);var visible=VisibleRect(hit.Owner);
            var center=rect.GetCenter();var physical=GetGlobalTransformWithCanvas()*center;
            if(visible.Encloses(rect)&&(TopWindowAt?.Invoke(physical)==Frame(c)))return rect;
        }
        return null;
    }
    private void InvalidDrop(string reason)
    {
        var origin=CurrentOrigin();State.InvalidDrop(reason,Now,Reduced,origin!=null?R(origin.Value):null,origin!=null);QueueRedraw();
    }
    private InventoryTarget? TargetAt(Vector2 pointer)
    {
        if(paintedItem==null)return null;
        var physical=GetGlobalTransformWithCanvas()*pointer;var targets=new List<InventoryTarget>();
        foreach(var c in Surfaces(GetParent()))
        {
            var target=((IInventoryInteractionSurface)c).InventoryTarget(paintedItem,State.Rotated,new Vector2(State.GrabFraction.X,State.GrabFraction.Y),c.GetGlobalTransformWithCanvas().AffineInverse()*physical);
            if(target!=null&&Usable(c,pointer,new Rect2(Vector2.Zero,c.Size)))targets.Add(target);
        }
        var source=SourceAt(pointer);
        if(source?.Item.Id==paintedItem.Id&&State.Gesture==InventoryGesture.Held)return new(source.Owner,source.Rect,InventoryTargetKind.Cancel,true,Priority:100);
        return targets.OrderByDescending(t=>t.Priority).FirstOrDefault();
    }
    private InventoryTarget? CandidateAt(Vector2 pointer)
    {
        if(WindowChrome(pointer))return null;
        var target=TargetAt(pointer);
        // A registered invalid tab must also win over Button.Pressed, preventing
        // a refused item drop from silently switching the original source pane.
        return OrdinaryControl(pointer)&&target?.NamedDestination!=true?null:target;
    }
    private string Context(InventoryItemView item)
    {
        var snapshot=Snapshot;var container=snapshot.Container(item.ContainerId);
        var pin=core.Connection?.Db.OwnItemDefinitionPins.Iter().FirstOrDefault(p=>p.ItemId==item.Id);
        return $"{core.Character?.Id}/{core.InventorySessionGeneration}/{core.GameplayEpoch}/{core.SharedWorldEpoch}/{snapshot.Revision}/{item.Id}/{item.DefinitionId}/{item.Definition?.Revision}/{pin?.DefinitionId}/{pin?.ItemRevision}/{pin?.WeaponRevision}/{item.ScopedRevision}/{item.ContainerId}/{container?.ScopedRevision}/{item.EquipmentSlot}/{item.X}/{item.Y}/{item.Rotated}";
    }
    private InventoryPointerSource Source(InventorySourceHit hit)=>new(hit.Item.Id,Context(hit.Item),Frame(hit.Owner)?.LayoutKey??"",hit.Item.Definition!.Width,hit.Item.Definition.Height,hit.Item.Rotated,R(InView(hit.Owner,hit.Rect)),Snapshot.Revision,hit.Owner.GetPath().ToString());
    public void CancelLocal(string reason=""){ordinaryPress=false;State.Move(N(Pointer));State.Cancel(true,reason);candidate=null;QueueRedraw();}
    public bool Rotate(){if(GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit)return false;var changed=State.Rotate();if(changed)QueueRedraw();return changed;}
    public bool TooltipsAllowed()=>State.TooltipsAllowed(Now,N(Pointer),core.InventoryPending);
    public bool OriginGhost(string id)=>State.OriginGhost(id,Now);
    internal void PickUpAndRotate(InventorySourceHit hit)
    {
        if(core.InventoryPending||hit.Item.Definition==null)return;
        paintedItem=hit.Item;State.Hold(Source(hit),N(Pointer),NVector.Zero,true);hit.Owner.GrabFocus();core.ReleaseControls();QueueRedraw();
    }
    public override void _Process(double delta)
    {
        Size=(GetParent() as Control)?.Size??Vector2.One;State.Move(N(Pointer));
        if(Capturing&&(Enabled?.Invoke()==false||ModalVisible(GetParent())))CancelLocal();
        if(Reduced)State.SkipGlide();
        if(State.Submission is {} previous&&(previous.SessionGeneration!=core.InventorySessionGeneration||previous.ActorId!=core.Character?.Id))State.Cancel(false);
        if(State.Submission is {} token&&core.InventoryAttempt is {} a&&token==Token(a))
        {
            State.Observe(token,a.IsPending,a.Phase==InventoryAttemptPhase.Confirmed?"":a.Reason);
            if(a.FreshAcceptedRows&&a.Stage!=InventoryAttemptStage.CargoTransfer)State.HideCosmetics();
        }
        if(!SourceStillCurrent())CancelLocal("Inventory or storage changed. Choose the current item again.");
        candidate=State.Gesture is (InventoryGesture.Held or InventoryGesture.Dragging)?CandidateAt(Pointer):null;
        if(Capturing||Animating||core.InventoryPending)CancelPassiveTooltips(GetParent());
        var paintActive=Capturing||Animating;
        if(paintActive||paintWasActive)QueueRedraw();
        paintWasActive=paintActive;
    }
    private static InventoryPaintToken Token(InventoryAttemptSnapshot a)=>new(a.Generation,a.OperationId,a.SessionGeneration,a.ActorId);
    private bool SourceStillCurrent()
    {
        if(State.Source is not {} source)return true;
        var item=Snapshot.Item(source.ItemId);
        return item?.Definition!=null&&Context(item)==source.Context&&Surfaces(GetParent()).Any(c=>c.GetPath().ToString()==source.SurfaceId&&((IInventoryInteractionSurface)c).InventoryOrigin(source.ItemId)!=null);
    }
    private bool OriginalHit(InventorySourceHit? hit)=>hit!=null&&hit.Item.Id==State.Source?.ItemId&&hit.Owner.GetPath().ToString()==State.Source?.SurfaceId;
    private bool Send(InventoryTarget t)
    {
        if(paintedItem==null)return false;var item=paintedItem;var revision=State.Source?.Revision??Snapshot.Revision;
        if(demo)
        {
            if(t.Kind==InventoryTargetKind.Grid)DemoInventory.Move(item.Id,t.ContainerId,t.X,t.Y,t.Rotated);
            else if(t.Kind==InventoryTargetKind.Equip)DemoInventory.Equip(item.Id);
            else if(t.Kind==InventoryTargetKind.Hotbar)DemoInventory.Assign(t.Slot,item.Id);
            else if(t.Kind==InventoryTargetKind.Transfer)
            {
                var destination=t.ContainerId.Length>0?t.ContainerId:DefaultCarriedDestination(Snapshot,item);
                if(destination==null||Snapshot.FirstPlacement(item.Id,destination) is not {} p)return false;
                DemoInventory.Move(item.Id,destination,p.X,p.Y,p.Rotated);
            }
            return true;
        }
        return t.Kind switch
        {
            InventoryTargetKind.Grid=>core.MoveItem(item.Id,t.ContainerId,t.X,t.Y,t.Rotated,revision),
            InventoryTargetKind.Equip=>core.EquipItem(item.Id,revision),
            InventoryTargetKind.Hotbar=>core.AssignHotbar(t.Slot,item.Id,revision),
            InventoryTargetKind.Transfer=>core.TransferItem(item.Id,t.ContainerId,revision),_=>false
        };
    }
    private void Drop(InventoryTarget? target)
    {
        if(!SourceStillCurrent())
        {CancelLocal("Inventory changed. Choose the current item again.");return;}
        if(target?.Valid!=true||core.InventoryPending)
        {InvalidDrop(core.InventoryPending?"Waiting for server confirmation…":target?.Reason??"Choose disclosed storage or an equipment slot.");return;}
        var rect=R(InView(target.Owner,target.Rect));
        if(target.Kind==InventoryTargetKind.Cancel){if(CurrentOrigin() is {} origin)State.Commit(null,R(origin),true,Now,Reduced);else CancelLocal();return;}
        if(target.Kind==InventoryTargetKind.Quick){target.Bind?.Invoke(paintedItem!.Id);State.Commit(null,rect,false,Now,Reduced);return;}
        var prior=core.InventoryAttempt?.Generation;
        if(!Send(target))
        {
            if(core.InventoryAttempt is {} failed&&failed.Generation!=prior)CancelLocal(failed.Reason);
            else InvalidDrop(core.InventoryMessage);
            return;
        }
        var attempt=!demo?core.InventoryAttempt:null;
        if(!demo&&(attempt==null||!AttemptMatches(attempt,target)))
        {CancelLocal("Request sent. Review its server confirmation before continuing.");return;}
        State.Commit(attempt!=null?Token(attempt):null,rect,target.Holds,Now,Reduced);core.ReleaseControls();QueueRedraw();
    }
    private bool AttemptMatches(InventoryAttemptSnapshot a,InventoryTarget target)
    {
        if(a.Source is not {} source||paintedItem?.Definition is not {} definition||a.SessionGeneration!=core.InventorySessionGeneration||a.ActorId!=core.Character?.Id||source.ItemId!=paintedItem.Id||source.DefinitionId!=paintedItem.DefinitionId||source.ItemRevision!=definition.Revision||source.ContainerId!=paintedItem.ContainerId||source.EquipmentSlot!=paintedItem.EquipmentSlot||source.X!=paintedItem.X||source.Y!=paintedItem.Y||source.Rotated!=paintedItem.Rotated||a.Expected.InventoryRevision!=State.Source?.Revision)return false;
        return target.Kind switch
        {
            InventoryTargetKind.Grid=>a.Kind==InventoryAttemptKind.Move&&a.Target.ContainerId==target.ContainerId&&a.Target.X==target.X&&a.Target.Y==target.Y&&a.Target.Rotated==target.Rotated,
            InventoryTargetKind.Equip=>a.Kind==InventoryAttemptKind.Equip&&a.Target.EquipmentSlot==paintedItem?.Definition?.EquipSlot&&a.Target.ItemId==paintedItem?.Id,
            InventoryTargetKind.Transfer=>a.Kind==InventoryAttemptKind.Transfer&&(target.ContainerId.Length>0?a.Target.ContainerId==target.ContainerId:
                a.Target.ContainerId.Length==0&&a.Target.DestinationKind==InventoryDestinationKind.Carried||
                a.Target.DestinationKind==InventoryDestinationKind.Exact&&Snapshot.Container(a.Target.ContainerId)?.Carried==true&&Snapshot.UsesScopedCargo(paintedItem.Id,"")),
            InventoryTargetKind.Hotbar=>a.Kind==InventoryAttemptKind.AssignHotbar&&a.Target.HotbarSlot==target.Slot&&a.Target.ItemId==paintedItem?.Id,_=>false
        };
    }
    internal static string? DefaultCarriedDestination(InventorySnapshot snapshot,InventoryItemView item)
    {
        var backpack=snapshot.Items.FirstOrDefault(row=>row.EquipmentSlot=="back");
        return snapshot.Containers.Where(c=>c.Kind=="grid"&&c.Carried&&c.Id!=item.ContainerId&&c.ParentItemId!=item.Id&&(c.ParentItemId==backpack?.Id||c.Id==snapshot.PocketsId))
            .OrderByDescending(c=>c.ParentItemId==backpack?.Id).FirstOrDefault(c=>snapshot.FirstPlacement(item.Id,c.Id)!=null)?.Id;
    }
    internal void QuickTransfer(InventorySourceHit hit,Vector2? eventPointer=null)
    {
        var snapshot=Snapshot;var source=snapshot.Container(hit.Item.ContainerId);var destination=OpenTransferDestination?.Invoke();
        var id=(hit.Item.EquipmentSlot.Length>0||source?.Carried==true)&&destination!=null?destination:"";
        var autoEquip=id.Length==0&&hit.Item.Definition?.EquipSlot=="back"&&!snapshot.Items.Any(i=>i.EquipmentSlot=="back")&&core.Connection?.Db.OwnGroundItems.Iter().Any(i=>i.Id==hit.Item.Id)==true;
        var fits=autoEquip||(id.Length>0?snapshot.FirstPlacement(hit.Item.Id,id)!=null:DefaultCarriedDestination(snapshot,hit.Item)!=null);
        paintedItem=hit.Item;State.Hold(Source(hit),N(eventPointer??Pointer),NVector.Zero);
        Drop(new(hit.Owner,hit.Rect,InventoryTargetKind.Transfer,fits,"No room in the disclosed destination.",ContainerId:id));
    }
    private void DoubleClick(InventorySourceHit hit,Vector2 pointer)
    {
        if(hit.Item.Definition?.EquipSlot.Length is not >0)return;
        paintedItem=hit.Item;State.Hold(Source(hit),N(pointer),NVector.Zero);
        if(hit.Item.EquipmentSlot.Length>0)
        {
            Drop(new(hit.Owner,hit.Rect,InventoryTargetKind.Transfer,DefaultCarriedDestination(Snapshot,hit.Item)!=null,"No carried storage space for this item."));
        }
        else Drop(new(hit.Owner,hit.Rect,InventoryTargetKind.Equip,true));
    }
    public override void _Input(InputEvent input)
    {
        if(!IsVisibleInTree()||Enabled?.Invoke()==false||ModalVisible(GetParent()))return;
        if(input is InputEventKey {Pressed:true,Echo:false,PhysicalKeycode:Key.Escape}&&(Capturing||Animating))
        {CancelLocal();GetViewport().SetInputAsHandled();return;}
        var pointer=input is InputEventMouse eventMouse?GetGlobalTransformWithCanvas().AffineInverse()*eventMouse.Position:Pointer;
        if(!SourceStillCurrent())CancelLocal("Inventory or storage changed. Choose the current item again.");
        State.Move(N(pointer));
        if(input is InputEventMouseMotion&&Capturing&&!ordinaryPress){GetViewport().SetInputAsHandled();QueueRedraw();return;}
        if(input is InputEventKey {Pressed:true,Echo:false,PhysicalKeycode:Key.R}&&Rotate()){GetViewport().SetInputAsHandled();return;}
        if(input is not InputEventMouseButton mouse)return;
        if(mouse.ButtonIndex==MouseButton.Right&&mouse.Pressed&&Capturing){CancelLocal();GetViewport().SetInputAsHandled();return;}
        if(mouse.ButtonIndex!=MouseButton.Left)return;
        if(mouse.Pressed)
        {
            ordinaryPress=false;
            State.NewPointerPress();
            var hit=SourceAt(pointer);
            if(mouse.DoubleClick&&hit!=null&&(State.Source==null||State.Source.ItemId==hit.Item.Id)&&!core.InventoryPending&&hit.Item.Definition?.EquipSlot.Length>0)
            {hit.Owner.GrabFocus();DoubleClick(hit,pointer);State.ConsumeNextRelease();GetViewport().SetInputAsHandled();return;}
            if(State.Gesture==InventoryGesture.Held)
            {
                if(WindowChrome(pointer)){CancelLocal();return;}
                var destination=CandidateAt(pointer);
                if(OrdinaryControl(pointer)&&destination?.NamedDestination!=true){ordinaryPress=true;return;}
                Drop(destination);State.ConsumeNextRelease();GetViewport().SetInputAsHandled();return;
            }
            if(hit?.Item.Definition==null||core.InventoryPending)return;
            paintedItem=hit.Item;if(State.Arm(Source(hit),N(pointer),false)){hit.Select?.Invoke();Frame(hit.Owner)?.BringToFront();hit.Owner.GrabFocus();core.ReleaseControls();GetViewport().SetInputAsHandled();}
        }
        else
        {
            if(ordinaryPress){ordinaryPress=false;return;}
            if(State.ConsumedRelease){State.Release(false,default);GetViewport().SetInputAsHandled();return;}
            if(!Capturing)return;var hit=SourceAt(pointer);
            if(State.Gesture==InventoryGesture.Armed&&mouse.ShiftPressed&&hit is {} transfer&&OriginalHit(transfer)){QuickTransfer(transfer,pointer);if(State.Capturing)State.Cancel(false,State.Reason);}
            else if(State.Release(OriginalHit(hit),hit!=null?R(InView(hit.Owner,hit.Rect)):default))Drop(CandidateAt(pointer));
            GetViewport().SetInputAsHandled();QueueRedraw();
        }
    }
    private object? AttemptFacts()
    {
        if(core.InventoryAttempt is not {} a)return null;
        return new{a.Generation,a.OperationId,a.StageOperationId,a.SessionGeneration,a.ActorId,kind=a.Kind.ToString(),phase=a.Phase.ToString(),stage=a.Stage.ToString(),
            a.CommittedAck,a.FreshAcceptedRows,a.AcceptedStages,a.IsPending,a.Source,a.Expected,a.Target,a.TransferTarget,a.AcceptedPlacement};
    }
    public object SmokeFacts()=>new{gesture=State.Gesture.ToString(),capturing=Capturing,animating=Animating,item=State.Source?.ItemId,rotation=State.Rotated,grab=new{x=State.GrabFraction.X,y=State.GrabFraction.Y},reason=State.Reason,submission=State.Submission,attempt=AttemptFacts(),prediction=State.Paint(Now),candidate=candidate==null?null:new{kind=candidate.Kind.ToString(),candidate.Valid,candidate.Reason,candidate.ContainerId,candidate.X,candidate.Y,candidate.Rotated,surfaceRole=candidate.SurfaceRole.ToString(),candidate.ActionLabel},destinations=Surfaces(GetParent()).OfType<ContainerDestination>().Select(c=>c.SmokeGeometry()).ToArray()};
    public override void _Draw()
    {
        var p=SiderealPalette.Current;
        if(candidate!=null&&State.Capturing){var rect=InView(candidate.Owner,candidate.Rect).Intersection(VisibleRect(candidate.Owner));var color=candidate.Valid?p.Accent:p.Danger;if(rect.HasArea()){DrawRect(rect,color with{A=.16f});DrawRect(rect,color,false,2);}}
        if(State.Paint(Now) is not {} paint||paintedItem?.Definition==null)return;
        var box=R(paint.Rect);ItemFrameStyle.Paint(this,box,ItemPresentation.Rarity(paintedItem.Definition),false,false,false,false,false,paint.Alpha);
        if(InventoryIcons.Texture(paintedItem.Definition) is {} texture)
        {
            var bounds=box.Grow(-5);var turns=InventoryIcons.QuarterTurns(paintedItem.Definition,texture,State.Rotated);
            DrawSetTransform(bounds.GetCenter(),turns*Mathf.Pi/2);var size=turns%2==1?new Vector2(bounds.Size.Y,bounds.Size.X):bounds.Size;
            DrawTextureRect(texture,InventoryIcons.Fit(texture,new Rect2(-size*.5f,size)),false,Colors.White with{A=paint.Alpha});DrawSetTransform(Vector2.Zero);
        }
        var note=paint.Pending?"Waiting for server confirmation…":candidate?.Valid==true?
            candidate.ActionLabel.Length>0?candidate.ActionLabel:"✓ Place item · R rotate":candidate?.Reason??State.Reason;
        if(note.Length==0)note="R rotate · Right-click cancels";
        DrawString(GetThemeFont("font","Label"),new Vector2(Math.Clamp(box.Position.X,4,Math.Max(4,Size.X-260)),Math.Clamp(box.End.Y+16,18,Math.Max(18,Size.Y-8))),note,HorizontalAlignment.Left,260,11,paint.Pending?p.Warning:candidate?.Valid==false?p.Danger:p.Accent);
    }
}
