using Godot;
using System;
using System.Linq;

namespace Sidereal.Ui;

/// <summary>Scoped native popup placement and readable overflow; engine windows keep their original input flags.</summary>
internal partial class NativePopupBounds : Node
{
    private readonly Window popup,boundsWindow;
    private readonly Control owner;
    private readonly bool passive,fitsOwnGeometry;
    private bool fitting,shown;
    private Vector2I lastSize,lastPosition;
    private Vector2 localAnchor;
    private float lastScale;
    private Label? defaultLabel;
    private float naturalLabelWidth;
    private ScrollContainer? colorScroll;
    public NativePopupBounds(Window popup,Control owner,Window boundsWindow,bool passive,bool fitsOwnGeometry)
    {this.popup=popup;this.owner=owner;this.boundsWindow=boundsWindow;this.passive=passive;this.fitsOwnGeometry=fitsOwnGeometry;Name="NativePopupBounds";}
    public override void _EnterTree()
    {popup.VisibilityChanged+=Shown;popup.SizeChanged+=Fit;boundsWindow.SizeChanged+=Fit;}
    public override void _Ready()
    {
        if(passive)defaultLabel=popup.GetChildren().OfType<Label>().FirstOrDefault();
        if(defaultLabel!=null)
        {naturalLabelWidth=defaultLabel.GetCombinedMinimumSize().X;defaultLabel.AutowrapMode=TextServer.AutowrapMode.WordSmart;defaultLabel.TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis;}
        if(popup.GetChildren().OfType<ColorPicker>().FirstOrDefault() is ColorPicker picker)
            Callable.From(()=>PreparePicker(picker)).CallDeferred();
        if(popup is PopupMenu)popup.WrapControls=false;
        SetProcess(false);
    }
    private void PreparePicker(ColorPicker picker)
    {
        if(!IsInsideTree()||!GodotObject.IsInstanceValid(picker)||popup.IsQueuedForDeletion())return;
        colorScroll=new ScrollContainer{Name="PopupContentScroll",FollowFocus=true,HorizontalScrollMode=ScrollContainer.ScrollMode.MaximizeFirst,VerticalScrollMode=ScrollContainer.ScrollMode.MaximizeFirst};
        popup.AddChild(colorScroll);picker.Reparent(colorScroll,false);Fit();
    }
    private Transform2D PhysicalTransform=>owner.GetViewport().GetFinalTransform()*owner.GetGlobalTransformWithCanvas();
    private void Shown()
    {
        shown=popup.Visible;SetProcess(shown&&!fitsOwnGeometry);
        if(!shown||popup.IsQueuedForDeletion()||fitsOwnGeometry)return;
        localAnchor=(Vector2)(popup.Position-boundsWindow.Position);Fit();
    }
    private void Fit()
    {
        if(fitting||!shown||!popup.Visible||popup.IsQueuedForDeletion()||fitsOwnGeometry||!IsInsideTree())return;
        fitting=true;
        try
        {
            var transform=PhysicalTransform;var requested=Math.Max(.01f,transform.Scale.X);
            var margin=Math.Max(1,(int)Math.Ceiling(8*requested));var available=(boundsWindow.Size-new Vector2I(margin*2,margin*2)).Max(Vector2I.One);
            // Remove the previous physical maximum before changing viewport scale/minimum.
            popup.MaxSize=Vector2I.Zero;
            popup.ContentScaleFactor=requested;
            var padding=popup.GetThemeStylebox("panel",popup is PopupMenu?"PopupMenu":"TooltipPanel").GetMinimumSize();
            if(colorScroll!=null)padding=popup.GetThemeStylebox("panel","PopupPanel").GetMinimumSize();
            var contentLimit=((Vector2)available/requested-padding).Max(Vector2.One);
            if(defaultLabel!=null)
            {
                defaultLabel.CustomMaximumSize=contentLimit;
                defaultLabel.CustomMinimumSize=new Vector2(Math.Min(naturalLabelWidth,contentLimit.X),0);
                var lineHeight=defaultLabel.GetThemeFont("font").GetHeight(defaultLabel.GetThemeFontSize("font_size"))+defaultLabel.GetThemeConstant("line_spacing");
                defaultLabel.MaxLinesVisible=Math.Max(1,(int)Math.Floor(contentLimit.Y/lineHeight));
            }
            if(colorScroll!=null)colorScroll.CustomMaximumSize=contentLimit;
            var logical=popup.GetContentsMinimumSize();
            popup.Size=new Vector2I((int)Math.Ceiling(logical.X*requested),(int)Math.Ceiling(logical.Y*requested)).Min(available).Max(Vector2I.One);
            popup.MaxSize=available;
            var min=boundsWindow.Position+new Vector2I(margin,margin);var max=boundsWindow.Position+boundsWindow.Size-popup.Size-new Vector2I(margin,margin);
            var position=(Vector2)boundsWindow.Position+localAnchor;
            if(passive)
            {
                var pointer=owner.GetViewport().GetFinalTransform()*owner.GetViewport().GetMousePosition()+(Vector2)owner.GetWindow().Position;
                var gap=ProjectSettings.GetSetting("display/mouse_cursor/tooltip_position_offset").AsVector2()*requested;
                position=pointer+gap;
                if(position.X+popup.Size.X>boundsWindow.Position.X+boundsWindow.Size.X-margin)position.X=pointer.X-popup.Size.X-gap.X;
                if(position.Y+popup.Size.Y>boundsWindow.Position.Y+boundsWindow.Size.Y-margin)position.Y=pointer.Y-popup.Size.Y-gap.Y;
            }
            else if(owner is BaseButton)
            {
                var start=transform*Vector2.Zero+(Vector2)owner.GetWindow().Position;var end=transform*owner.Size+(Vector2)owner.GetWindow().Position;
                position=new Vector2(start.X,end.Y);
                if(owner is ColorPickerButton)
                {position.X=(start.X+end.X-popup.Size.X)/2;position.Y=end.Y+margin;if(position.Y+popup.Size.Y>boundsWindow.Position.Y+boundsWindow.Size.Y-margin)position.Y=start.Y-popup.Size.Y-margin;}
            }
            popup.Position=new Vector2I((int)Math.Ceiling(Math.Clamp(position.X,min.X,Math.Max(min.X,max.X))),(int)Math.Ceiling(Math.Clamp(position.Y,min.Y,Math.Max(min.Y,max.Y))));
            lastSize=boundsWindow.Size;lastPosition=boundsWindow.Position;lastScale=requested;
        }
        finally{fitting=false;}
    }
    public override void _Process(double delta)
    {if(boundsWindow.Size!=lastSize||boundsWindow.Position!=lastPosition||PhysicalTransform.Scale.X!=lastScale)Fit();}
    public override void _ExitTree()
    {shown=false;SetProcess(false);popup.VisibilityChanged-=Shown;popup.SizeChanged-=Fit;if(GodotObject.IsInstanceValid(boundsWindow))boundsWindow.SizeChanged-=Fit;}
}
