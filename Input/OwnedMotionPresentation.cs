// Exact source-renderer motion from a356; independent source-executed expectations
// are in Tests/Fixtures/owned-motion-golden.json. This class grants no gameplay authority.
using System;
namespace Sidereal.Native.Input;

/// <summary>
/// Stable accepted viewer/scene context, not a render-cell epoch or a motion/location revision.
/// The caller clears on denied/missing context. An accepted stair deck transition keeps
/// this context so the source terminal-stair snap marker can survive until the commit.
/// Never use GameplayEpoch (changes on equipment/seat/capture) as this key.
/// </summary>
public sealed record OwnedMotionContext(string ViewerIdentity, string ActorId, string PresentedShipId,
    string? InstanceId, string? VisitId, string? AdmissionIdentity);
public readonly record struct OwnedMotionPoint(double X, double Y, double Z);

/// <summary>
/// Output of an existing accepted traversal-frame resolver, not a predicted path.
/// Source instance/visit/deck/phase/finite-range guards must run before constructing
/// an InTransit frame. StairX/Y/Phase/Kind come from that same accepted stair row.
/// </summary>
public sealed record OwnedTraversalFrame(bool InTransit, double WalkingElevation,
    OwnedMotionPoint? AcceptedPositionM = null, string? Kind = null,
    double? StairX = null, double? StairY = null, string? Phase = null);
public sealed record OwnedMotionAccepted(double X, double Y, double Heading, double LocalX, double LocalY,
    bool Interior, bool ReducedMotion, double StandingElevationM,
    bool Seated = false, double SeatLift = 0, bool Construction = true,
    double? SupportElevationM = null, OwnedTraversalFrame? Traversal = null);

/// <summary>
/// Distinct accepted input basis, smoothed local fallback origin, and actual rendered
/// body position. The body may be overridden by accepted traversal coordinates while
/// source aim fallback still uses DisplayLocalXY and BodyLocalPosition.Y.
/// </summary>
public sealed record OwnedMotionFrame(double X, double Y, double Heading, double LocalX, double LocalY,
    double AcceptedHeading, OwnedMotionPoint BodyLocalPosition, double DeltaSeconds, double MotionCoefficient,
    double TransitionCoefficient, bool Walking, double? TravelHeading, double? StairTravelHeading, ulong ContextGeneration);

/// <summary>Renderer-only own motion; no reducers, grants, simulation writes or extrapolation.</summary>
public sealed class OwnedMotionPresentation
{
    public const string SourceRevision = "a35632deedf210cf43f64c43cb741d841f4a1ce0";
    private OwnedMotionContext? context;
    private bool initialized;
    private ulong generation,contextGeneration;
    private double x, y, heading, localX, localY;
    private (double X, double Y)? lastStairPosition;
    private double? stairTravelHeading;
    public string? UnsupportedReason { get; private set; }

    public void Clear()
    {
        context = null; initialized = false; lastStairPosition = null;
        stairTravelHeading = null; UnsupportedReason = null;
    }
    private static bool Finite(double value) => double.IsFinite(value);
    private static double AngleDelta(double from, double to) => Math.Atan2(Math.Sin(to-from), Math.Cos(to-from));
    private static bool Valid(OwnedMotionAccepted value) =>
        Finite(value.X) && Finite(value.Y) && Finite(value.Heading) && Finite(value.LocalX) && Finite(value.LocalY) &&
        Finite(value.StandingElevationM) && Finite(value.SeatLift) &&
        (value.SupportElevationM == null || Finite(value.SupportElevationM.Value)) &&
        (value.Traversal == null || Finite(value.Traversal.WalkingElevation) &&
            (value.Traversal.AcceptedPositionM == null || Finite(value.Traversal.AcceptedPositionM.Value.X) &&
             Finite(value.Traversal.AcceptedPositionM.Value.Y) && Finite(value.Traversal.AcceptedPositionM.Value.Z)) &&
            (value.Traversal.StairX == null || Finite(value.Traversal.StairX.Value)) &&
            (value.Traversal.StairY == null || Finite(value.Traversal.StairY.Value)));

    /// <summary>
    /// null owner/accepted data clears immediately and returns no display. Invalid data
    /// is declined without throwing through the Godot frame loop. Source engine delta
    /// is nonnegative; the boundary additionally rejects invalid/negative native input.
    /// </summary>
    public OwnedMotionFrame? Step(OwnedMotionContext? owner, OwnedMotionAccepted? accepted, double deltaSeconds)
    {
        if (owner == null || accepted == null) { Clear(); return null; }
        if (!Finite(deltaSeconds) || deltaSeconds < 0 || !Valid(accepted))
        { Clear(); UnsupportedReason = "Accepted owned presentation contains unsupported coordinates."; return null; }
        if (context != owner) { Clear(); context = owner; contextGeneration=++generation; }
        UnsupportedReason = null;
        var dt = Math.Min(deltaSeconds, .1);
        var motion = 1-Math.Exp(-dt*18);
        var transition = accepted.ReducedMotion ? 1 : 1-Math.Exp(-dt*6);
        if (!initialized)
        {
            x=accepted.X; y=accepted.Y; heading=accepted.Heading;
            localX=accepted.LocalX; localY=accepted.LocalY; initialized=true;
        }
        heading += AngleDelta(heading,accepted.Heading)*motion;
        if (double.Hypot(accepted.LocalX-localX,accepted.LocalY-localY)>2)
        { localX=accepted.LocalX; localY=accepted.LocalY; }
        x += (accepted.X-x)*motion; y += (accepted.Y-y)*motion;
        localX += (accepted.LocalX-localX)*motion; localY += (accepted.LocalY-localY)*motion;

        var traversal=accepted.Traversal;
        var elevation=traversal?.WalkingElevation ?? accepted.StandingElevationM;
        if (accepted.Construction && traversal?.InTransit!=true && accepted.SupportElevationM is {} support)
            elevation=support;
        var stair=traversal is {InTransit:true,Kind:"stair",StairX:not null,StairY:not null};
        var stairMoving=stair && traversal!.Phase is "walking" or "stepping" or "returning";
        if (stair)
        {
            var sx=traversal!.StairX!.Value;var sy=traversal.StairY!.Value;
            if (lastStairPosition is {} previous && double.Hypot(sx-previous.X,sy-previous.Y)>1e-6)
                stairTravelHeading=Math.Atan2(sx-previous.X,sy-previous.Y);
            lastStairPosition=(sx,sy);
        }
        else if (lastStairPosition != null)
        {
            // Source native stair terminal commit snaps even when the final relocation
            // is smaller than 2m; do not reset the context on that accepted deck change.
            localX=accepted.LocalX; localY=accepted.LocalY;
            lastStairPosition=null; stairTravelHeading=null;
        }
        var dx=accepted.LocalX-localX;var dy=accepted.LocalY-localY;
        var walking=!accepted.Seated && (stairMoving || double.Hypot(dx,dy)>.015 && traversal?.InTransit!=true);
        var travelHeading=stairMoving?stairTravelHeading:walking?Math.Atan2(dx,dy):(double?)null;
        var body=new OwnedMotionPoint(localX,elevation+(accepted.Seated?accepted.SeatLift:0),-localY);
        if (traversal?.AcceptedPositionM is {} position)
            body=new OwnedMotionPoint(position.X,position.Z,-position.Y);
        // Finite inputs can still overflow a subtraction or seat-height sum.
        // Decline that presentation instead of forwarding NaN to a scene transform.
        if (!Finite(x) || !Finite(y) || !Finite(heading) || !Finite(localX) || !Finite(localY) ||
            !Finite(body.X) || !Finite(body.Y) || !Finite(body.Z) ||
            (travelHeading.HasValue && !Finite(travelHeading.Value)))
        { Clear(); UnsupportedReason = "Accepted owned presentation exceeds native numerical range."; return null; }
        return new(x,y,heading,localX,localY,accepted.Heading,body,dt,motion,transition,walking,travelHeading,stairTravelHeading,contextGeneration);
    }
    // Source own avatar facing updates even while its mesh is hidden in flight view.
    // Its placement is unbound: travel first, accepted aim next, seat facing last.
    public static double BodyYaw(double current, double? travel, double? aim, double? seatFacing)
    {
        if(!Finite(current)||(travel.HasValue&&!Finite(travel.Value))||(aim.HasValue&&!Finite(aim.Value))||
            (seatFacing.HasValue&&!Finite(seatFacing.Value)))throw new ArgumentOutOfRangeException(nameof(current));
        return seatFacing??(aim.HasValue?-aim.Value:travel.HasValue?-travel.Value:current);
    }
    public static double DesiredCameraAlpha(OwnedMotionFrame display,bool interior,double orbit) =>
        interior?Math.PI-display.Heading+orbit:Math.PI/2;
    public static OwnedMotionPoint CameraTarget(OwnedMotionFrame display,double blend,
        double displayedDeckHalf,double initialDeckHalf,double centerX,double centerY,bool construction=true,bool eva=false)
    {
        var t=Math.Clamp((displayedDeckHalf-2)/(Math.Max(3,initialDeckHalf)-2),0,1);
        var actorBlend=eva?1:blend*(1-.4*t*t*(3-2*t));
        var targetX=centerX*(1-actorBlend)+display.BodyLocalPosition.X*actorBlend;
        var targetY=centerY*(1-actorBlend)-display.BodyLocalPosition.Z*actorBlend;
        var c=Math.Cos(display.Heading);var s=Math.Sin(display.Heading);
        return new(targetX*c-targetY*s,.8*blend+(construction?display.BodyLocalPosition.Y:0),-(targetX*s+targetY*c));
    }
}
