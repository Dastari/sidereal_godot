using System;
using System.Collections.Generic;
namespace Sidereal.Native;
public readonly record struct CrewDisplayedMotion(double X,double Y,double Height,double Speed,bool Moving,double Yaw);
/// <summary>Exact bounded browser crew interpolation; positions are accepted samples and never feed simulation.</summary>
public sealed class CrewMotionReplay
{
    private readonly record struct Sample(double At,double X,double Y,double Height);
    private readonly List<Sample> samples=new();private readonly double delay;private double? yaw,lastAt;
    public CrewMotionReplay(double delaySeconds=.075){delay=delaySeconds;}
    public void Push(double at,double x,double y,double height)
    {
        if(samples.Count>0)
        {
            var newest=samples[^1];if(newest.X==x&&newest.Y==y&&newest.Height==height)return;
            if(Math.Sqrt(Math.Pow(x-newest.X,2)+Math.Pow(y-newest.Y,2))>2)samples.Clear();
            else if(at-newest.At>.25)samples.Add(new(at-.05,newest.X,newest.Y,newest.Height));
        }
        samples.Add(new(at,x,y,height));while(samples.Count>8)samples.RemoveAt(0);
    }
    public CrewDisplayedMotion Read(double now,bool seated,bool dead,bool aimActive,double aimAngle,double seatFacing)
    {
        var target=now-delay;double x=0,y=0,h=0,vx=0,vy=0;
        if(samples.Count>0)
        {
            var i=samples.Count-1;while(i>0&&samples[i-1].At>=target)i--;var b=samples[i];var a=samples[Math.Max(0,i-1)];
            var k=a==b||target>=b.At?1:target<=a.At?0:Math.Clamp((target-a.At)/(b.At-a.At),0,1);x=a.X+(b.X-a.X)*k;y=a.Y+(b.Y-a.Y)*k;h=a.Height+(b.Height-a.Height)*k;
            if(a!=b&&b.At>a.At&&target<b.At+.12){vx=(b.X-a.X)/(b.At-a.At);vy=(b.Y-a.Y)/(b.At-a.At);}
        }
        var speed=Math.Sqrt(vx*vx+vy*vy);var moving=!seated&&!dead&&speed>.35;var wanted=yaw;
        if(seated)wanted=seatFacing;else if(!dead&&aimActive)wanted=-aimAngle;else if(!dead&&moving)wanted=-Math.Atan2(vx,vy);wanted??=0;
        var dt=lastAt.HasValue?Math.Max(0,now-lastAt.Value):0;lastAt=now;
        if(!yaw.HasValue||seated||aimActive)yaw=wanted;else{double Wrap(double a)=>Math.Atan2(Math.Sin(a),Math.Cos(a));yaw=Wrap(yaw.Value+Wrap(wanted.Value-yaw.Value)*(1-Math.Exp(-dt*14)));}
        return new(x,y,h,speed,moving,yaw.Value);
    }
}
