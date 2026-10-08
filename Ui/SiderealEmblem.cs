using Godot;
using System;

namespace Sidereal.Ui;

/// <summary>The browser's code-native orbital wordmark emblem.</summary>
public partial class SiderealEmblem : Control
{
    public SiderealEmblem(){CustomMinimumSize=new Vector2(80,60);MouseFilter=MouseFilterEnum.Ignore;}
    public override void _Draw()
    {
        var scale=Math.Min(Size.X/120,Size.Y/80);var center=Size*.5f;
        DrawCircle(center,27*scale,new Color("8faaff") with {A=.4f});
        DrawArc(center,27*scale,0,Mathf.Tau,64,new Color("7cacff"),3*scale,true);
        var points=new Vector2[97];var angle=-25*Mathf.Pi/180;
        for(var i=0;i<points.Length;i++){var t=i*Mathf.Tau/(points.Length-1);points[i]=center+new Vector2(56*Mathf.Cos(t),16*Mathf.Sin(t)).Rotated(angle)*scale;}
        DrawPolyline(points,SiderealPalette.Current.Accent,3*scale,true);
    }
}
