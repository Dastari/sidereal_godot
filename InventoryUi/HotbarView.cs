using System;
using System.Linq;
using Godot;
using Sidereal.Ui;
using Sidereal.Native;

namespace Sidereal.InventoryUi;

public partial class HotbarView : HBoxContainer
{
    private readonly ClientCore core;
    private readonly bool demo;
    private readonly HotbarSlot[] slots;
    private readonly Label actionCaption,quickCaption;
    public System.Collections.Generic.IReadOnlyList<Control> VisualSlots => slots;
    private readonly string[] quick = new string[2];
    public event Action<string>? InspectRequested;
    public event Action? OpenInventoryRequested;
    public string AssignmentItemId {get;set;}="";
    public HotbarView(ClientCore core, bool demo)
    {
        this.core=core;this.demo=demo;InventoryPresentation.EnsureCatalogue();SizeFlagsVertical=SizeFlags.ExpandFill;AddThemeConstantOverride("separation",10);
        var main=new VBoxContainer {SizeFlagsHorizontal=SizeFlags.ExpandFill,SizeFlagsVertical=SizeFlags.ExpandFill};main.AddThemeConstantOverride("separation",5);
        actionCaption=UiKit.Label("ACTION BAR",10);actionCaption.ThemeTypeVariation="AccentLabel";main.AddChild(actionCaption);
        var quickColumn=new VBoxContainer {SizeFlagsHorizontal=SizeFlags.ExpandFill,SizeFlagsVertical=SizeFlags.ExpandFill};quickColumn.AddThemeConstantOverride("separation",5);
        quickCaption=UiKit.Label("QUICK SLOTS",10);quickCaption.ThemeTypeVariation="AccentLabel";quickColumn.AddChild(quickCaption);
        var mainSlots=new HBoxContainer {SizeFlagsHorizontal=SizeFlags.ExpandFill,SizeFlagsVertical=SizeFlags.ExpandFill};mainSlots.AddThemeConstantOverride("separation",5);main.AddChild(mainSlots);
        var quickSlots=new HBoxContainer {SizeFlagsHorizontal=SizeFlags.ExpandFill,SizeFlagsVertical=SizeFlags.ExpandFill};quickSlots.AddThemeConstantOverride("separation",5);quickColumn.AddChild(quickSlots);
        var mainFrame=UiKit.Panel(main,0,"CompactFramePanel");mainFrame.SizeFlagsHorizontal=SizeFlags.ExpandFill;mainFrame.SizeFlagsStretchRatio=4;AddChild(mainFrame);
        var quickFrame=UiKit.Panel(quickColumn,0,"CompactFramePanel");quickFrame.SizeFlagsHorizontal=SizeFlags.ExpandFill;AddChild(quickFrame);
        slots = new HotbarSlot[10];
        for(byte i=0;i<10;i++){var index=i;slots[i]=new HotbarSlot(core,demo,i,()=>ActivateSlot(index),id=>quick[index-8]=id);slots[i].SizeFlagsVertical=SizeFlags.ExpandFill;(i<8?mainSlots:quickSlots).AddChild(slots[i]);}
        Refresh();
    }
    public void SetCompact(bool compact)
    {
        actionCaption.Visible=quickCaption.Visible=!compact;
        foreach(var slot in slots)slot.CustomMinimumSize=new Vector2(compact?20:32,compact?40:48);
    }
    private void QuickInspect(int index)
    {
        if(index is <0 or >1||core.InventoryPending)return;
        if(InventoryPresentation.Read(core,demo).Item(AssignmentItemId)!=null){quick[index]=AssignmentItemId;return;}
        var id=quick[index];if(!string.IsNullOrEmpty(id))InspectRequested?.Invoke(id);else OpenInventoryRequested?.Invoke();
    }
    private void ActivateSlot(byte slot)
    {
        if(slot>=8){QuickInspect(slot-8);return;}if(slot>=5||core.InventoryPending)return;
        var snapshot=InventoryPresentation.Read(core,demo);
        if(snapshot.Item(AssignmentItemId)?.Definition?.EquipSlot.Length>0){if(demo)DemoInventory.Assign(slot,AssignmentItemId);else core.AssignHotbar(slot,AssignmentItemId,snapshot.Revision);return;}
        if(snapshot.Hotbar.TryGetValue(slot,out var item)&&item.Length>0){if(demo)DemoInventory.Equip(item);else core.ActivateHotbar(slot,snapshot.Revision);}
        else OpenInventoryRequested?.Invoke();
    }
    public void Refresh()
    {
        var snapshot=InventoryPresentation.Read(core,demo);
        foreach(var (definition,index) in new[]{("medkit",0),("power-cell",1)})
            if(snapshot.Item(quick[index])==null)quick[index]=snapshot.Items.FirstOrDefault(i=>i.DefinitionId==definition)?.Id??"";
        var assignment=snapshot.Item(AssignmentItemId);
        for(var i=0;i<slots.Length;i++)slots[i].Refresh(snapshot,i>=8?quick[i-8]:null,assignment);
    }
    public override void _UnhandledInput(InputEvent input)
    {
        if(!IsVisibleInTree()||input is not InputEventKey {Pressed:true,Echo:false} key||GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit||GetViewport().GuiIsDragging())return;
        for(Node? parent=GetParent();parent!=null;parent=parent.GetParent())if(parent is SiderealUi {GameplayShortcutBlocked:true})return;
        if(key.PhysicalKeycode is Key.Key9 or Key.Key0){QuickInspect(key.PhysicalKeycode==Key.Key9?0:1);GetViewport().SetInputAsHandled();return;}
        var number=(long)key.PhysicalKeycode;if(number<'1'||number>'5')return;var slot=(byte)(number-'1');
        ActivateSlot(slot);
        GetViewport().SetInputAsHandled();
    }
}

internal partial class HotbarSlot : Control
{
    private readonly ClientCore core;private readonly bool demo;private readonly byte slot;private readonly Action inspect;private readonly Action<string> bindQuick;
    private InventorySnapshot snapshot=InventorySnapshot.Empty;private InventoryItemView? item,assignmentItem;private bool hover;
    private bool Reserved=>slot is >=5 and <8;private bool Quick=>slot>=8;
    public HotbarSlot(ClientCore core,bool demo,byte slot,Action inspect,Action<string> bindQuick)
    {
        this.core=core;this.demo=demo;this.slot=slot;this.inspect=inspect;this.bindQuick=bindQuick;
        CustomMinimumSize=new Vector2(32,48);SizeFlagsHorizontal=SizeFlags.ExpandFill;MouseFilter=MouseFilterEnum.Stop;FocusMode=Reserved?FocusModeEnum.None:FocusModeEnum.All;
        MouseEntered+=()=>{hover=true;QueueRedraw();};MouseExited+=()=>{hover=false;QueueRedraw();};FocusEntered+=QueueRedraw;FocusExited+=QueueRedraw;
    }
    public void Refresh(InventorySnapshot next,string? quick,InventoryItemView? assignment=null)
    {
        snapshot=next;assignmentItem=assignment;item=Quick?next.Item(quick??""):next.Hotbar.TryGetValue(slot,out var id)?next.Item(id):null;
        TooltipText=!ItemDrag.TooltipsAllowed(this,core)?"":Reserved?"Reserved action slot. No operation is installed.":assignment!=null&&(Quick||assignment.Definition?.EquipSlot.Length>0)?$"Press {(Quick?(slot==8?9:0):slot+1)} or click to assign {assignment.Name}.":Quick? $"{(slot==8?9:0)}: {item?.Name??"Empty quick slot"}\nClick to inspect in inventory. Drag an item here to assign it.":
            item!=null?ItemPresentation.Tooltip(core,item)+$"\nPress {slot+1} or click to equip. Right-click clears this reference.":$"Action {slot+1}\nDrag an equippable item here.";QueueRedraw();
    }
    public override GodotObject _MakeCustomTooltip(string text)=>text.Length==0||!ItemDrag.TooltipsAllowed(this,core)?null!:assignmentItem!=null&&(Quick||assignmentItem.Definition?.EquipSlot.Length>0)?UiKit.Tooltip("Assign item",text):item==null?UiKit.Tooltip(Reserved?"Reserved action":"Unassigned slot",text):new ItemTooltip(this,core,item,Quick?"Click to inspect · Drag an item here to assign it":$"Press {slot+1} or click to equip · Right-click clears this reference");
    public override bool _CanDropData(Vector2 at,Variant data)=>!Reserved&&ItemDrag.Payload(data)&&ItemDrag.Current is {} drag&&drag.Core==core&&drag.Demo==demo&&!core.InventoryPending&&(Quick||drag.Item.Definition?.EquipSlot.Length>0);
    public override void _DropData(Vector2 at,Variant data)
    { if(!_CanDropData(at,data)||ItemDrag.Current is not {} drag)return;if(Quick)bindQuick(drag.Item.Id);else if(demo)DemoInventory.Assign(slot,drag.Item.Id);else core.AssignHotbar(slot,drag.Item.Id,drag.Revision); }
    public override void _GuiInput(InputEvent input)
    {
        if(Reserved)return;
        if(input.IsActionPressed("ui_accept")){Activate();AcceptEvent();return;}
        if(input is not InputEventMouseButton {Pressed:true} mouse)return;
        if(mouse.ButtonIndex==MouseButton.Right){if(Quick)bindQuick("");else if(demo)DemoInventory.Assign(slot,"");else core.AssignHotbar(slot,"",snapshot.Revision);AcceptEvent();}
        else if(mouse.ButtonIndex==MouseButton.Left){Activate();AcceptEvent();}
    }
    private void Activate(){if(!core.InventoryPending)inspect();}
    public override void _Draw()
    {
        var p=SiderealPalette.Current;var font=GetThemeFont("font","Label");var selected=item?.EquipmentSlot.Length>0;
        ItemFrameStyle.Paint(this,new Rect2(Vector2.Zero,Size),ItemPresentation.Rarity(item?.Definition),selected,hover,HasFocus(),Reserved||(!demo&&core.InventoryPending),item==null);
        DrawString(font,new Vector2(6,14),Quick?(slot==8?"9":"0"):(slot+1).ToString(),HorizontalAlignment.Left,-1,10,Reserved?p.Muted:p.Text);
        if(InventoryIcons.Texture(item?.Definition) is {} texture)DrawTextureRect(texture,InventoryIcons.Fit(texture,new Rect2(6,10,Size.X-12,Math.Max(18,Size.Y-18))),false);
        else if(!Reserved&&!Quick)
        {
            var glyph=slot switch{0=>"»",1=>"⊕",2=>"◇",3=>"◎",_=>"+"};DrawString(font,new Vector2(Size.X*.32f,Size.Y*.62f),glyph,HorizontalAlignment.Left,-1,20,p.Accent with {A=.7f});
            if(Size.X>=40)DrawString(font,new Vector2(5,Size.Y-6),"ASSIGN",HorizontalAlignment.Left,Size.X-10,8,p.Muted);
        }
    }
}
