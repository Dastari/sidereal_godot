using System;
using System.Numerics;

namespace Sidereal.Native.Input;

public enum InventoryGesture { Idle, Armed, Held, Dragging }
public readonly record struct InventoryPointerRect(float X,float Y,float Width,float Height)
{
    public bool Contains(Vector2 p)=>p.X>=X&&p.Y>=Y&&p.X<X+Width&&p.Y<Y+Height;
    public Vector2 Fraction(Vector2 p)=>new(Math.Clamp((p.X-X)/Math.Max(1,Width),0,1),Math.Clamp((p.Y-Y)/Math.Max(1,Height),0,1));
    public static InventoryPointerRect Mix(InventoryPointerRect a,InventoryPointerRect b,float k)=>new(a.X+(b.X-a.X)*k,a.Y+(b.Y-a.Y)*k,a.Width+(b.Width-a.Width)*k,a.Height+(b.Height-a.Height)*k);
}
public sealed record InventoryPointerSource(string ItemId,string Context,string WindowId,int Width,int Height,bool Rotated,InventoryPointerRect Rect,ulong Revision=0,string SurfaceId="");
public readonly record struct InventoryPaintToken(ulong Generation,string OperationId,ulong SessionGeneration,string ActorId);
public sealed record InventoryPointerPaint(InventoryPointerSource Source,InventoryPointerRect Rect,float Alpha,bool Pending);

/// <summary>One local inventory pointer gesture. It never writes an inventory snapshot or a domain lock.</summary>
public sealed class InventoryInteraction
{
    public const float DragThreshold=6;
    public const double GlideSeconds=.1, PredictionSeconds=1.5;
    public static bool BlocksPopup(bool visible,bool popupPanel,bool exclusive,bool noFocus,bool mousePassthrough)
        =>visible&&!(popupPanel&&!exclusive&&noFocus&&mousePassthrough);
    public InventoryGesture Gesture {get;private set;}
    public InventoryPointerSource? Source {get;private set;}
    public Vector2 Pointer {get;private set;}
    public Vector2 GrabFraction {get;private set;}
    public bool Rotated {get;private set;}
    public string Reason {get;private set;}="";
    public InventoryPaintToken? Submission {get;private set;}
    public bool Capturing=>Gesture!=InventoryGesture.Idle;
    public bool ConsumedRelease {get;private set;}
    private Vector2 press;
    private Vector2? quiet;
    private sealed record Glide(InventoryPointerSource Source,InventoryPointerRect From,InventoryPointerRect To,double Start,bool Fade);
    private sealed record Prediction(InventoryPointerSource Source,InventoryPointerRect? Rect,double Until);
    private Glide? glide;
    private Prediction? prediction;
    public bool Animating(double now)=>glide!=null||prediction is {} p&&now<p.Until;
    public bool OriginGhost(string item,double now)=>(Source?.ItemId==item&&Gesture is (InventoryGesture.Held or InventoryGesture.Dragging))||(prediction is {} pending&&pending.Source.ItemId==item&&now<pending.Until);
    public bool TooltipsAllowed(double now,Vector2 pointer,bool domainPending)
    {
        if(Capturing||Animating(now)||domainPending)return false;
        if(quiet is {} q){if(Vector2.Distance(q,pointer)<=3)return false;quiet=null;}
        return true;
    }
    public bool Arm(InventoryPointerSource source,Vector2 point,bool blocked)
    {
        if(blocked||Capturing||ConsumedRelease)return false;
        Source=source;Pointer=press=point;Rotated=source.Rotated;GrabFraction=source.Rect.Fraction(point);
        Gesture=InventoryGesture.Armed;Reason="";quiet=null;glide=null;return true;
    }
    public void Move(Vector2 point)
    {
        Pointer=point;
        if(Gesture==InventoryGesture.Armed&&Vector2.Distance(point,press)>DragThreshold)Gesture=InventoryGesture.Dragging;
    }
    public bool Release(bool originEnabled,InventoryPointerRect originRect)
    {
        if(ConsumedRelease){ConsumedRelease=false;return false;}
        if(Gesture==InventoryGesture.Armed)
        {
            if(originEnabled&&originRect.Contains(Pointer)){GrabFraction=originRect.Fraction(Pointer);Gesture=InventoryGesture.Held;}
            else Cancel(false);
            return false;
        }
        return Gesture==InventoryGesture.Dragging;
    }
    public void Hold(InventoryPointerSource source,Vector2 pointer,Vector2 fraction,bool rotate=false)
    {
        Cancel(false);Source=source;Pointer=pointer;Rotated=source.Rotated;GrabFraction=Vector2.Clamp(fraction,Vector2.Zero,Vector2.One);Gesture=InventoryGesture.Held;
        if(rotate)Rotate();
    }
    public bool Rotate()
    {
        if(Gesture is not (InventoryGesture.Held or InventoryGesture.Dragging))return false;
        Rotated=!Rotated;GrabFraction=new(GrabFraction.Y,GrabFraction.X);return true;
    }
    public InventoryPointerRect HeldRect()
    {
        if(Source is not {} s)return default;
        var w=(Rotated?s.Height:s.Width)*48-4;var h=(Rotated?s.Width:s.Height)*48-4;
        return new(Pointer.X-GrabFraction.X*w,Pointer.Y-GrabFraction.Y*h,w,h);
    }
    public void ConsumeNextRelease()=>ConsumedRelease=true;
    public void NewPointerPress()=>ConsumedRelease=false;
    public void HideCosmetics(){glide=null;prediction=null;}
    public void SkipGlide()=>glide=null;
    public void InvalidDrop(string reason,double now,bool reducedMotion,InventoryPointerRect? returnRect=null,bool returnVisible=true)
    {
        Reason=reason;
        if(Gesture!=InventoryGesture.Dragging)return;
        if(Source is {} s&&!reducedMotion&&returnVisible)glide=new(s,HeldRect(),returnRect??s.Rect,now,false);
        ClearGesture();quiet=Pointer;
    }
    public void Commit(InventoryPaintToken? token,InventoryPointerRect? landing,bool holds,double now,bool reducedMotion)
    {
        if(Source is not {} source)return;
        var from=HeldRect();
        if(!reducedMotion)glide=new(source,from,landing??from,now,!holds);
        prediction=token is {}?new(source,holds?landing:null,now+PredictionSeconds):null;
        Submission=token;ClearGesture();Reason="";quiet=Pointer;
    }
    public void Observe(InventoryPaintToken token,bool pending,string reason)
    {
        if(Submission!=token)return;
        if(pending)return;
        Submission=null;prediction=null;glide=null;Reason=reason;
    }
    public void Cancel(bool consumeRelease=true,string reason="")
    {
        if(consumeRelease&&Gesture is (InventoryGesture.Armed or InventoryGesture.Dragging))ConsumedRelease=true;
        ClearGesture();glide=null;prediction=null;quiet=Pointer;Reason=reason;
        // A sent command remains owned by Core; cosmetic cancellation does not unlock it.
    }
    private void ClearGesture(){Gesture=InventoryGesture.Idle;Source=null;}
    public InventoryPointerPaint? Paint(double now)
    {
        if(glide is {} g)
        {
            var t=(float)Math.Clamp((now-g.Start)/GlideSeconds,0,1);var k=1-MathF.Pow(1-t,3);
            if(t<1)return new(g.Source,InventoryPointerRect.Mix(g.From,g.To,k),g.Fade ? .92f*(1-k) : .92f+.08f*k,Submission!=null);
            glide=null;
        }
        if(Gesture is (InventoryGesture.Held or InventoryGesture.Dragging)&&Source is {} held)return new(held,HeldRect(),.9f,false);
        if(prediction is {} p)
        {
            if(now>=p.Until){prediction=null;return null;}
            if(p.Rect is {} rect)return new(p.Source,rect,1,true);
        }
        return null;
    }
}
