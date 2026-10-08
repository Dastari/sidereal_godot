using Godot;
using System.Collections.Generic;

namespace Sidereal.Ui;

/// <summary>The browser hud-icons heart path, expressed in its original 24-unit space.</summary>
public partial class VitalIcon : Control
{
    public VitalIcon(){CustomMinimumSize=new Vector2(19,19);MouseFilter=MouseFilterEnum.Ignore;}
    public override void _Ready(){SiderealPalette.Current.Changed+=QueueRedraw;}
    public override void _ExitTree(){SiderealPalette.Current.Changed-=QueueRedraw;}
    public override void _Draw()
    {
        var points=new List<Vector2>();var start=new Vector2(0,9);
        void Curve(Vector2 a,Vector2 b,Vector2 end)
        {
            for(var i=0;i<12;i++){var t=i/12f;var u=1-t;points.Add(start*u*u*u+a*3*u*u*t+b*3*u*t*t+end*t*t*t);}
            start=end;
        }
        Curve(new(-3,6),new(-10,1),new(-10,-4));Curve(new(-10,-10),new(-3,-11),new(0,-5));
        Curve(new(3,-11),new(10,-10),new(10,-4));Curve(new(10,1),new(3,6),new(0,9));
        var scale=Mathf.Min(Size.X,Size.Y)/24;for(var i=0;i<points.Count;i++)points[i]=Size*.5f+points[i]*scale;
        DrawColoredPolygon(points.ToArray(),SiderealPalette.Current.Health);
    }
}
