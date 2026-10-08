using System;
using System.Numerics;
using System.Linq;
namespace Sidereal.Native;
/// <summary>The released browser analytic two-bone solver, applied only to the displayed clip pose.</summary>
public static class CrewKinematics
{
    public static Matrix4x4[] Globals(CrewAnimationBank bank,CrewBonePose[] pose)
    {
        var result=new Matrix4x4[pose.Length];var visited=new bool[pose.Length];
        Matrix4x4 At(int i){if(visited[i])return result[i];var p=pose[i];var local=Matrix4x4.CreateScale(p.Scale)*Matrix4x4.CreateFromQuaternion(p.Rotation)*Matrix4x4.CreateTranslation(p.Position);var parent=bank.Parents[i];result[i]=parent<0?local:local*At(parent);visited[i]=true;return result[i];}
        for(var i=0;i<pose.Length;i++)At(i);return result;
    }
    private static Quaternion Between(Vector3 a,Vector3 b)
    {
        if(a.LengthSquared()<1e-10||b.LengthSquared()<1e-10)return Quaternion.Identity;a=Vector3.Normalize(a);b=Vector3.Normalize(b);var d=Math.Clamp(Vector3.Dot(a,b),-1,1);
        if(d>.999999)return Quaternion.Identity;var cross=Vector3.Cross(a,b);if(d<-.999999){cross=Vector3.Cross(Vector3.UnitX,a);if(cross.LengthSquared()<1e-6)cross=Vector3.Cross(Vector3.UnitY,a);}
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(cross),MathF.Acos(d));
    }
    public static void Rotate(CrewAnimationBank bank,CrewBonePose[] pose,int bone,Quaternion rotation)
    {
        var globals=Globals(bank,pose);var original=globals[bone];var pos=original.Translation;
        var desired=original*Matrix4x4.CreateTranslation(-pos)*Matrix4x4.CreateFromQuaternion(rotation)*Matrix4x4.CreateTranslation(pos);
        if(bank.Parents[bone]>=0){if(!Matrix4x4.Invert(globals[bank.Parents[bone]],out var inverse))return;desired*=inverse;}
        if(Matrix4x4.Decompose(desired,out _,out var q,out _))pose[bone]=pose[bone] with{Rotation=Quaternion.Normalize(q)};
    }
    public static double Solve(CrewAnimationBank bank,CrewBonePose[] pose,string upperName,string lowerName,string endName,Matrix4x4 target,bool matchRotation=true)
    {
        var upper=System.Array.IndexOf(bank.BoneNames,upperName);var lower=System.Array.IndexOf(bank.BoneNames,lowerName);var end=System.Array.IndexOf(bank.BoneNames,endName);if(upper<0||lower<0||end<0)return double.NaN;
        var globals=Globals(bank,pose);var s=globals[upper].Translation;var e=globals[lower].Translation;var w=globals[end].Translation;var goal=target.Translation;var l1=Vector3.Distance(s,e);var l2=Vector3.Distance(e,w);var to=goal-s;if(to.LengthSquared()<1e-8||l1<1e-6||l2<1e-6)return Vector3.Distance(w,goal);
        var distance=Math.Clamp(to.Length(),1e-4f,(l1+l2)*.9995f);var direction=Vector3.Normalize(to);var a=(l1*l1-l2*l2+distance*distance)/(2*distance);var h=MathF.Sqrt(Math.Max(0,l1*l1-a*a));var pole=e-s;pole-=direction*Vector3.Dot(pole,direction);if(pole.LengthSquared()<1e-8)pole=Vector3.UnitZ-direction*direction.Z;if(pole.LengthSquared()<1e-8)pole=Vector3.UnitX;pole=Vector3.Normalize(pole);var elbow=s+direction*a+pole*h;
        Rotate(bank,pose,upper,Between(e-s,elbow-s));globals=Globals(bank,pose);e=globals[lower].Translation;w=globals[end].Translation;Rotate(bank,pose,lower,Between(w-e,goal-e));
        if(matchRotation&&Matrix4x4.Decompose(target,out _,out var targetQ,out _))
        {
            globals=Globals(bank,pose);var desired=Matrix4x4.CreateFromQuaternion(targetQ)*Matrix4x4.CreateTranslation(globals[end].Translation);if(bank.Parents[end]>=0&&Matrix4x4.Invert(globals[bank.Parents[end]],out var inverse))desired*=inverse;
            if(Matrix4x4.Decompose(desired,out _,out var q,out _))pose[end]=pose[end] with{Rotation=Quaternion.Normalize(q)};
        }
        return Vector3.Distance(Globals(bank,pose)[end].Translation,goal);
    }
    public static void Seat(CrewAnimationBank bank,CrewBonePose[] pose,double scale,double lift,double lean,double footSupport,double forward,double? footForward)
    {
        var spine=System.Array.IndexOf(bank.BoneNames,"spine");if(spine>=0&&lean!=0)Rotate(bank,pose,spine,Quaternion.CreateFromAxisAngle(Vector3.UnitX,(float)lean));
        foreach(var side in new[]{"L","R"})
        {
            var index=System.Array.IndexOf(bank.BoneNames,"foot."+side);if(index<0)continue;var target=Globals(bank,pose)[index];target.M42=(float)(3d/32+(footSupport-lift)/scale);if(footForward.HasValue)target.M43=(float)((forward-footForward.Value)/scale);
            Solve(bank,pose,"thigh."+side,"shin."+side,"foot."+side,target);
        }
    }
    public static void GroundClamp(CrewAnimationBank bank,CrewBonePose[] pose)
    {
        foreach(var side in new[]{"L","R"}){var index=System.Array.IndexOf(bank.BoneNames,"foot."+side);if(index<0)continue;var target=Globals(bank,pose)[index];if(target.M42>=3f/32)continue;target.M42=3f/32;Solve(bank,pose,"thigh."+side,"shin."+side,"foot."+side,target);}
    }
}

/// <summary>Browser stance anchors in the parent ship frame. This modifies displayed bones only.</summary>
public sealed class CrewFootPlanting
{
    private sealed class Foot { public Vector3? Lock, From; public float Weight; }
    private readonly Foot[] feet = { new(), new() };
    private readonly float restAnkle, band, maxDrift, release;
    public double Error { get; private set; }
    public CrewFootPlanting(double scale = .9, double restAnkle = 3d / 32, double stanceBand = .012, double maxDrift = .12, double releaseSeconds = .08)
    { this.restAnkle = (float)restAnkle; band = (float)stanceBand; this.maxDrift = (float)(maxDrift * scale); release = (float)releaseSeconds; }
    public void Reset()
    { Error = 0; foreach (var foot in feet) { foot.Lock = foot.From = null; foot.Weight = 0; } }
    public void Step(CrewAnimationBank bank, CrewBonePose[] pose, Matrix4x4 modelToFrame, double dt, bool plant = true)
    {
        if (!plant) Reset(); Error = 0;
        if (!Matrix4x4.Invert(modelToFrame, out var frameToModel)) { Reset(); return; }
        var sides = new[] { "L", "R" };
        var bones = sides.Select(side => System.Array.IndexOf(bank.BoneNames, "foot." + side)).ToArray();
        if (bones.Any(bone => bone < 0)) { Reset(); return; }
        var globals = CrewKinematics.Globals(bank, pose);
        var heights = bones.Select(bone => globals[bone].Translation.Y).ToArray();
        for (var i = 0; i < feet.Length; i++)
        {
            globals = CrewKinematics.Globals(bank, pose);
            var goal = globals[bones[i]]; var animated = goal.Translation;
            var planted = plant && heights.Where((_, other) => other != i).All(height => heights[i] <= height + 1e-4f) && animated.Y < restAnkle + band;
            var clamped = animated; clamped.Y = Math.Max(clamped.Y, restAnkle);
            var here = Vector3.Transform(clamped, modelToFrame); var state = feet[i]; var previous = state.Lock ?? state.From;
            if (previous.HasValue && Vector3.Distance(previous.Value, here) > maxDrift)
            { state.Lock = state.From = null; state.Weight = 0; }
            if (planted) { state.Lock ??= here; state.Weight = 1; state.From = null; }
            else if (state.Lock.HasValue)
            {
                state.From ??= state.Lock;
                state.Weight = Math.Max(0, state.Weight - (float)Math.Max(0, dt) / release);
                if (state.Weight == 0) state.Lock = state.From = null;
            }
            var pin = state.Lock ?? state.From; var target = clamped;
            if (pin.HasValue && state.Weight > 0)
            {
                var pinned = Vector3.Transform(pin.Value, frameToModel); pinned.Y = clamped.Y;
                target = Vector3.Lerp(clamped, pinned, state.Weight);
            }
            if (Vector3.Distance(target, animated) < 1e-4f) continue;
            goal.Translation = target;
            Error = Math.Max(Error, CrewKinematics.Solve(bank, pose, "thigh." + sides[i], "shin." + sides[i], "foot." + sides[i], goal));
        }
    }
}
