using Godot;
using System;
using System.Collections.Generic;
using Sidereal.Native;
using Sidereal.Ui;

namespace Sidereal.InventoryUi;

/// <summary>One passive hover card for grid, equipment and action references; all values remain pinned data.</summary>
internal partial class ItemTooltip : PanelContainer
{
    private readonly string rarity;
    private readonly Control owner;
    private readonly Window ownerWindow;
    private readonly ItemTooltipIcon icon;
    private readonly Rect2? itemAnchor;
    private readonly List<Action> paletteBindings=new();
    private bool fitQueued,fitting;
    public ItemTooltip(Control owner,ClientCore core,InventoryItemView item,string hint="",Rect2? itemAnchor=null)
    {
        this.owner=owner;ownerWindow=owner.GetWindow();this.itemAnchor=itemAnchor;
        Name="ItemTooltip";MouseFilter=MouseFilterEnum.Ignore;rarity=ItemPresentation.Rarity(item.Definition);
        var scale=Math.Max(.01f,owner.GetGlobalTransformWithCanvas().Scale.X);
        var width=Math.Min(360,Math.Max(180,owner.GetViewportRect().Size.X/scale-28));
        CustomMinimumSize=new Vector2(width,0);Theme=SiderealPalette.Current.CreateTheme();
        var frame=new StyleBoxEmpty {ContentMarginLeft=14,ContentMarginRight=14,ContentMarginTop=14,ContentMarginBottom=14};AddThemeStyleboxOverride("panel",frame);
        var column=new VBoxContainer();column.AddThemeConstantOverride("separation",10);AddChild(column);
        var header=new HBoxContainer();header.AddThemeConstantOverride("separation",10);column.AddChild(header);
        icon=new ItemTooltipIcon(item.Definition,rarity){CustomMinimumSize=new Vector2(94,110),SizeFlagsVertical=SizeFlags.ShrinkBegin};header.AddChild(icon);
        var titleColumn=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill,CustomMinimumSize=new Vector2(Math.Max(1,width-132),0)};titleColumn.AddThemeConstantOverride("separation",5);header.AddChild(titleColumn);
        var title=Text(item.Name.ToUpperInvariant(),17,Math.Max(1,width-132));titleColumn.AddChild(title);
        paletteBindings.Add(()=>title.AddThemeColorOverride("font_color",SiderealPalette.Current.Rarity(rarity)));
        var details=ItemPresentation.ReadDetails(core,item);
        var category=Text(details.Category.ToUpperInvariant(),12,Math.Max(1,width-132));category.ThemeTypeVariation="AccentLabel";titleColumn.AddChild(category);
        var badge=Text(rarity.ToUpperInvariant(),11,0);badge.AutowrapMode=TextServer.AutowrapMode.Off;
        var badgeFrame=new PanelContainer{CustomMinimumSize=new Vector2(Math.Min(94,width-132),19),SizeFlagsHorizontal=SizeFlags.ShrinkBegin,MouseFilter=MouseFilterEnum.Ignore};badgeFrame.AddChild(badge);titleColumn.AddChild(badgeFrame);
        paletteBindings.Add(()=>
        {
            var edge=SiderealPalette.Current.Rarity(rarity);badge.AddThemeColorOverride("font_color",edge.Lightened(.45f));
            badgeFrame.AddThemeStyleboxOverride("panel",new StyleBoxFlat{BgColor=new Color(edge,.13f*SiderealPalette.Current.Opacity),BorderColor=edge,BorderWidthLeft=1,BorderWidthRight=1,BorderWidthTop=1,BorderWidthBottom=1,ContentMarginLeft=6,ContentMarginRight=6,ContentMarginTop=2,ContentMarginBottom=2});
        });
        if(details.Description.Length>0){var description=Text(details.Description,13,width-28);description.ThemeTypeVariation="MutedLabel";column.AddChild(description);}
        foreach(var stat in details.Stats)
        {
            var row=new HBoxContainer();row.AddThemeConstantOverride("separation",6);column.AddChild(row);
            var label=Text(stat.Label,12,Math.Min(90,(width-60)*.4f));label.ThemeTypeVariation="MutedLabel";row.AddChild(label);
            row.AddChild(new ProgressBar{ShowPercentage=false,Value=stat.Fraction*100,SizeFlagsHorizontal=SizeFlags.ExpandFill,SizeFlagsVertical=SizeFlags.ShrinkCenter,CustomMinimumSize=new Vector2(20,8)});
            var value=Text(stat.Value,12,Math.Min(72,(width-60)*.32f));value.HorizontalAlignment=HorizontalAlignment.Right;row.AddChild(value);
        }
        if(details.Status!=null){var status=Text(details.Status,12,width-28);status.ThemeTypeVariation="WarningLabel";column.AddChild(status);}
        column.AddChild(new HSeparator());
        if(item.Definition is {} definition)
        {
            var metadata=Text($"{definition.MassKg:0.##} kg dry mass    {definition.Width} × {definition.Height} cells",13,width-28);metadata.ThemeTypeVariation="MutedLabel";column.AddChild(metadata);
            if(definition.EquipSlot.Length>0){var slot=Text("Equipment slot: "+definition.EquipSlot,12,width-28);slot.ThemeTypeVariation="MutedLabel";column.AddChild(slot);}
        }
        if(hint.Length>0){var action=Text(hint,11,width-28);action.ThemeTypeVariation="MutedLabel";column.AddChild(action);}
        IgnoreInput(this);ApplyPalette();
    }
    private static Label Text(string text,int size,float width)
    {var label=new Label{Text=text,AutowrapMode=TextServer.AutowrapMode.WordSmart,CustomMinimumSize=new Vector2(width,0),MouseFilter=MouseFilterEnum.Ignore,SizeFlagsHorizontal=SizeFlags.ExpandFill,ThemeTypeVariation="TooltipLabel"};label.AddThemeFontSizeOverride("font_size",size);return label;}
    private static void IgnoreInput(Node node)
    {if(node is Control control)control.MouseFilter=MouseFilterEnum.Ignore;foreach(var child in node.GetChildren())IgnoreInput(child);}
    private void ApplyPalette(){Theme=SiderealPalette.Current.CreateTheme();foreach(var update in paletteBindings)update();icon.QueueRedraw();QueueRedraw();QueueFit();}
    private void QueueFit()
    {
        if(!IsInsideTree()||fitQueued||fitting)return;fitQueued=true;Callable.From(FitPopup).CallDeferred();
    }
    private void FitPopup()
    {
        fitQueued=false;
        if(!IsInsideTree()||!GodotObject.IsInstanceValid(owner)||GetParent() is not PopupPanel popup)return;
        var logical=GetCombinedMinimumSize();if(logical.X<=0||logical.Y<=0)return;
        var viewport=owner.GetViewportRect().Size;var requested=Math.Max(.01f,owner.GetGlobalTransformWithCanvas().Scale.X);
        var margin=8*requested;
        var factor=Math.Max(.01f,Math.Min(requested,Math.Min((viewport.X-margin*2)/logical.X,(viewport.Y-margin*2)/logical.Y)));
        var physical=new Vector2I((int)Math.Ceiling(logical.X*factor),(int)Math.Ceiling(logical.Y*factor));
        fitting=true;
        try
        {
            // Embedded PopupPanel measures its child in physical coordinates. Keep
            // one explicit logical-to-physical transform, as the rest of the UI does.
            popup.WrapControls=false;popup.MinSize=Vector2I.Zero;popup.ContentScaleMode=Window.ContentScaleModeEnum.Disabled;
            popup.Size=physical;SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);Size=logical;Scale=Vector2.One*factor;
            var origin=popup.IsEmbedded()?Vector2.Zero:(Vector2)ownerWindow.Position;
            var min=origin+Vector2.One*margin;var max=origin+viewport-(Vector2)physical-Vector2.One*margin;
            var local=itemAnchor??new Rect2(Vector2.Zero,owner.Size);var transform=owner.GetGlobalTransformWithCanvas();
            var anchorStart=origin+transform*local.Position;var anchorEnd=origin+transform*local.End;var gap=12*requested;
            var right=anchorEnd.X+gap;var x=right+physical.X<origin.X+viewport.X-margin?right:anchorStart.X-physical.X-gap;
            popup.Position=new Vector2I((int)Math.Ceiling(Math.Clamp(x,min.X,Math.Max(min.X,max.X))),(int)Math.Ceiling(Math.Clamp(anchorStart.Y,min.Y,Math.Max(min.Y,max.Y))));
        }
        finally{fitting=false;}
    }
    public override void _Ready()
    {
        if(GetParent() is PopupPanel popup)popup.AddThemeStyleboxOverride("panel",new StyleBoxEmpty());
        SiderealPalette.Current.Changed+=ApplyPalette;ownerWindow.SizeChanged+=QueueFit;MinimumSizeChanged+=QueueFit;Resized+=QueueFit;QueueFit();
    }
    public override void _ExitTree()
    {SiderealPalette.Current.Changed-=ApplyPalette;if(GodotObject.IsInstanceValid(ownerWindow))ownerWindow.SizeChanged-=QueueFit;MinimumSizeChanged-=QueueFit;Resized-=QueueFit;}
    public override void _Draw()=>ItemFrameStyle.Paint(this,new Rect2(Vector2.Zero,Size),rarity);
}

internal partial class ItemTooltipIcon : Control
{
    private readonly ItemDefinition? definition;private readonly string rarity;
    public ItemTooltipIcon(ItemDefinition? definition,string rarity){this.definition=definition;this.rarity=rarity;MouseFilter=MouseFilterEnum.Ignore;}
    public override void _Draw()
    {ItemFrameStyle.Paint(this,new Rect2(Vector2.Zero,Size),rarity,empty:definition==null);if(InventoryIcons.Texture(definition) is {} texture)DrawTextureRect(texture,InventoryIcons.Fit(texture,new Rect2(5,5,Size.X-10,Size.Y-10)),false);}
}
