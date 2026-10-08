using System;
using System.Collections.Generic;
using System.Linq;
namespace Sidereal.Native;
/// <summary>Browser shared-world's eight accepted samples and 100ms delay; never extrapolates.</summary>
public sealed class SpaceMotionHistory
{
    public readonly record struct Pose(string SystemId,ulong Tick,double X,double Y,double Vx,double Vy,double Heading,double Omega);
    public readonly record struct Sample(Pose Pose,bool Stale);
    private readonly List<(Pose Pose,double Received)> samples=new();
    public int Count=>samples.Count;
    public void Remember(Pose row,double receivedMs)
    {
        if(row.SystemId.Length==0||!double.IsFinite(row.X+row.Y+row.Vx+row.Vy+row.Heading+row.Omega+receivedMs))return;
        if(samples.Count>0){var last=samples[^1].Pose;if(last.SystemId==row.SystemId&&row.Tick<last.Tick)return;if(last.SystemId!=row.SystemId||row.Tick-last.Tick>1200)samples.Clear();else if(last==row)return;}
        if(samples.Count>0&&samples[^1].Pose.Tick==row.Tick)samples[^1]=(row,receivedMs);else samples.Add((row,receivedMs));
        if(samples.Count>8)samples.RemoveRange(0,samples.Count-8);
    }
    public Sample? At(double nowMs,double delayMs=100)
    {
        if(samples.Count==0||!double.IsFinite(nowMs+delayMs)||delayMs<0||delayMs>1000)return null;
        var latest=samples[^1];var elapsed=Math.Max(0,nowMs-latest.Received);var target=Math.Min(0,(elapsed-delayMs)/50);var left=samples[0];var right=left;
        for(var i=1;i<samples.Count;i++){right=samples[i];if(-(double)(latest.Pose.Tick-right.Pose.Tick)>=target)break;left=right;}
        var start=-(double)(latest.Pose.Tick-left.Pose.Tick);var end=-(double)(latest.Pose.Tick-right.Pose.Tick);var t=end>start?Math.Clamp((target-start)/(end-start),0,1):0;
        double L(double a,double b)=>a+(b-a)*t;
        return new Sample(latest.Pose with {X=L(left.Pose.X,right.Pose.X),Y=L(left.Pose.Y,right.Pose.Y),Vx=L(left.Pose.Vx,right.Pose.Vx),Vy=L(left.Pose.Vy,right.Pose.Vy),Heading=left.Pose.Heading+SpaceMath.AngleDelta(left.Pose.Heading,right.Pose.Heading)*t,Omega=L(left.Pose.Omega,right.Pose.Omega)},elapsed>1000);
    }
    public void Clear()=>samples.Clear();
}
