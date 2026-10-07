using System;
using System.IO;
using System.Text.Json;
using Sidereal.Native.Input;

/// <summary>Compared with executed source slices, independently of the C# helper.</summary>
public static class OwnedMotionTests
{
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static double Number(JsonElement row, string key, double fallback = 0)
        => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;
    private static bool Flag(JsonElement row, string key, bool fallback = false)
        => row.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
    private static string? Text(JsonElement row, string key)
        => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? OptionalNumber(JsonElement row, string key)
        => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    private static void Near(double actual, double expected, string field)
    {
        // At ±1e12 the source rounds through f64 arithmetic too: keep the expected
        // absolute precision rather than weakening to a relative kilometre tolerance.
        Require(double.IsFinite(actual) && Math.Abs(actual-expected) <= 1e-9,
            $"Owned source motion differs at {field}: {actual:R} versus {expected:R}");
    }
    private static void Point(OwnedMotionPoint actual, JsonElement expected, string field)
    { Near(actual.X,Number(expected,"x"),field+" X"); Near(actual.Y,Number(expected,"y"),field+" Y"); Near(actual.Z,Number(expected,"z"),field+" Z"); }
    private static OwnedMotionContext Context(int generation)
        => new("socket-"+generation,"actor","ship","instance-a","visit-a","system/admission");

    public static void RunYaw(string path)
    {
        var bytes=File.ReadAllBytes(path);
        Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()=="b2c2b30c0669df24b304b17bb53967d3f98d366d836c21acfa9fa79309e29a1e","Source own-yaw fixture pin changed");
        using var document=JsonDocument.Parse(bytes);
        Require(Text(document.RootElement,"sourceCommit")==OwnedMotionPresentation.SourceRevision,"Own facing fixture source differs");
        var count=0;
        foreach(var fixture in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var current=Number(fixture,"initialYaw");
            foreach(var frame in fixture.GetProperty("frames").EnumerateArray())
            {
                if(Flag(frame.GetProperty("input"),"reset"))current=0;
                Near(current,Number(frame,"beforeYaw"),"source yaw reset/retention");
                var arguments=frame.GetProperty("arguments");
                current=OwnedMotionPresentation.BodyYaw(current,OptionalNumber(arguments,"travel"),OptionalNumber(arguments,"aim"),OptionalNumber(arguments,"seatFacing"));
                Near(current,Number(frame,"expectedYaw"),"source placement/aim/seat facing");count++;
            }
        }
        Require(count==47,"Source own-facing comparison omitted frames");
        Console.WriteLine("Source own facing10cases47frames match hidden flight/stop retention, aim/seat priority and true scene resets.");
    }

    public static void Run(string goldenPath)
    {
        var lifetime=new OwnedMotionPresentation();
        var owner=new OwnedMotionContext("stable-viewer","same-actor","ship","same-instance","visit-a","system");
        var lifetimeInput=new OwnedMotionAccepted(0,0,0,0,0,true,false,.1875);
        var first=lifetime.Step(owner,lifetimeInput,.016)!;
        var continued=lifetime.Step(owner,lifetimeInput with{LocalX=1,Heading=1},.016)!;
        Require(continued.ContextGeneration==first.ContextGeneration&&continued.LocalX>0&&continued.LocalX<1,"Continuous disclosed scene restarted motion");
        // Successful same-identity token renewal has no socket field in this context.
        var renewed=lifetime.Step(owner,lifetimeInput with{LocalX=1,Heading=1},.016)!;
        Require(renewed.ContextGeneration==first.ContextGeneration&&renewed.LocalX>continued.LocalX,"Stable viewer renewal snapped displayed pose");
        var revisited=lifetime.Step(owner with{VisitId="visit-b"},lifetimeInput with{LocalX=1,Heading=1},.016)!;
        Require(revisited.ContextGeneration!=first.ContextGeneration&&revisited.LocalX==1&&revisited.Heading==1,"Same actor/instance new visit did not expose a shared scene-reset generation");
        lifetime.Step(null,null,.016);
        var redisclosed=lifetime.Step(owner with{VisitId="visit-b"},lifetimeInput,.016)!;
        Require(redisclosed.ContextGeneration!=revisited.ContextGeneration,"Disclosure loss reused an old crew/camera reset generation");
        using var document = JsonDocument.Parse(File.ReadAllText(goldenPath));
        var root = document.RootElement;
        Require(root.GetProperty("sourceCommit").GetString()==OwnedMotionPresentation.SourceRevision,"Owned source motion reference changed");
        Require(root.GetProperty("sourceFiles").GetProperty("packages/render/src/index.ts").GetString()==
            "25d575340d09618dfa405eca3414bcaf0c218625bea5f146d9c5399ce2aeb321","Owned source motion renderer hash changed");
        Require(root.GetProperty("extractedBlocks").GetProperty("displayedMotion").GetProperty("sha256").GetString()==
            "69a9c16958f3f6110ad3678631752c8fd9b1bf8f0ebc959e3f8be6fb48ee746e","Owned motion block was not the exact source block");
        var frames=0;var cases=0;
        foreach (var fixture in root.GetProperty("cases").EnumerateArray())
        {
            cases++;var name=fixture.GetProperty("name").GetString()!;var config=fixture.GetProperty("config");
            var model=new OwnedMotionPresentation();var generation=0;var initializedCamera=false;
            var orbit=Number(config,"orbit",.45);var initialDeckZoom=Number(config,"deckZoom",12);
            var shownZoom=initialDeckZoom;var shownFlightZoom=55d;var blend=0d;var alpha=0d;var beta=0d;
            var centerX=Number(config,"centerX",1.5);var centerY=Number(config,"centerY",-2);
            var frameIndex=0;
            foreach (var sample in fixture.GetProperty("frames").EnumerateArray())
            {
                frames++;frameIndex++;var id=name+"/"+frameIndex;var input=sample.GetProperty("input");
                if(Flag(input,"clear"))
                { Require(model.Step(null,null,0)==null,"Disclosure loss retained owned display"); initializedCamera=false;continue; }
                if(Flag(input,"reset"))
                {generation++;initializedCamera=false;shownZoom=initialDeckZoom;shownFlightZoom=55;}
                var accepted=sample.GetProperty("accepted");var expected=sample.GetProperty("expected");
                var reduced=Flag(accepted,"reducedMotion");var interior=Flag(accepted,"interior");
                OwnedTraversalFrame? traversal=null;
                // This is explicit prevalidated fixture input produced by the SOURCE
                // resolver. It is not the expected motion/body output under test.
                var resolved=sample.GetProperty("resolvedTraversal");
                if(resolved.ValueKind==JsonValueKind.Object)
                {
                    var raw=accepted.TryGetProperty("constructionTraversal",out var row)?row:default;
                    OwnedMotionPoint? position=null;
                    if(resolved.TryGetProperty("acceptedPositionM",out var p)&&p.ValueKind==JsonValueKind.Array)
                        position=new(p[0].GetDouble(),p[1].GetDouble(),p[2].GetDouble());
                    var stair=raw.ValueKind==JsonValueKind.Object&&Text(raw,"kind")=="stair";
                    traversal=new(Flag(resolved,"inTransit"),Number(resolved,"walkingElevation"),position,
                        stair?"stair":null,stair?OptionalNumber(raw,"x"):null,stair?OptionalNumber(raw,"y"):null,
                        raw.ValueKind==JsonValueKind.Object?Text(raw,"phase"):null);
                }
                var seated=Flag(accepted,"seated");var lift=0d;
                if(accepted.TryGetProperty("seatContact",out var contact)&&contact.ValueKind==JsonValueKind.Object)lift=Number(contact,"lift");
                var value=new OwnedMotionAccepted(Number(accepted,"x"),Number(accepted,"y"),Number(accepted,"heading"),
                    Number(accepted,"localX"),Number(accepted,"localY"),interior,reduced,.1875,seated,lift,
                    SupportElevationM:OptionalNumber(accepted,"constructionSupportElevation"),Traversal:traversal);
                var shown=model.Step(Context(generation),value,Number(input,"delta"));
                Require(shown!=null,"Valid accepted owned pose was refused: "+id);
                var display=expected.GetProperty("displayed");
                Near(shown!.X,Number(display,"x"),id+" world X");Near(shown.Y,Number(display,"y"),id+" world Y");
                Near(shown.Heading,Number(display,"heading"),id+" heading");Near(shown.LocalX,Number(display,"localX"),id+" local X");Near(shown.LocalY,Number(display,"localY"),id+" local Y");
                Near(shown.AcceptedHeading,value.Heading,id+" accepted input basis");
                Near(shown.DeltaSeconds,Number(expected,"delta"),id+" capped delta");
                Near(shown.MotionCoefficient,Number(expected,"motion"),id+" motion18");Near(shown.TransitionCoefficient,Number(expected,"transition"),id+" transition6");
                Point(shown.BodyLocalPosition,expected.GetProperty("body"),id+" body");
                Require(shown.Walking==Flag(expected,"walking"),"Owned source walking state differs: "+id);
                var heading=OptionalNumber(expected,"movementHeading");
                Require(shown.TravelHeading.HasValue==heading.HasValue,"Owned source travel heading presence differs: "+id);
                if(heading.HasValue)Near(shown.TravelHeading!.Value,heading.Value,id+" travel heading");
                var stairHeading=OptionalNumber(expected,"stairTravelHeading");
                Require(shown.StairTravelHeading.HasValue==stairHeading.HasValue,"Owned source terminal stair marker differs: "+id);
                if(stairHeading.HasValue)Near(shown.StairTravelHeading!.Value,stairHeading.Value,id+" stair heading");
                if(!initializedCamera)
                {initializedCamera=true;blend=interior?1:0;alpha=interior?Math.PI-value.Heading+orbit:Math.PI/2;beta=interior?Math.Acos(1/Math.Sqrt(3)):Flag(accepted,"inspect")?.6:.015;}
                blend+=((interior?1:0)-blend)*shown.TransitionCoefficient;
                var zoomCoefficient=1-Math.Exp(-12*shown.DeltaSeconds);
                shownZoom=reduced?initialDeckZoom:shownZoom+(initialDeckZoom-shownZoom)*zoomCoefficient;
                shownFlightZoom=reduced?55:shownFlightZoom+(55-shownFlightZoom)*zoomCoefficient;
                var desiredAlpha=OwnedMotionPresentation.DesiredCameraAlpha(shown,interior,orbit);
                alpha+=Math.Atan2(Math.Sin(desiredAlpha-alpha),Math.Cos(desiredAlpha-alpha))*shown.TransitionCoefficient;
                beta+=((interior?Math.Acos(1/Math.Sqrt(3)):Flag(accepted,"inspect")?.6:.015)-beta)*shown.TransitionCoefficient;
                Near(blend,Number(expected,"blend"),id+" camera blend");
                Near(shownZoom,Number(expected,"displayedZoom"),id+" shown deck zoom");Near(shownFlightZoom,Number(expected,"displayedFlightZoom"),id+" shown flight zoom");
                var camera=expected.GetProperty("camera");Near(desiredAlpha,Number(camera,"desiredAlpha"),id+" desired alpha");
                Near(alpha,Number(camera,"alpha"),id+" displayed alpha");Near(beta,Number(camera,"beta"),id+" displayed beta");
                Point(OwnedMotionPresentation.CameraTarget(shown,blend,shownZoom,initialDeckZoom,centerX,centerY),camera.GetProperty("target"),id+" camera target");
                var horizontal=Number(input,"horizontal",.6);var vertical=Number(input,"vertical",.8);
                var angle=alpha+shown.AcceptedHeading;var scale=1/Math.Max(1,double.Hypot(horizontal,vertical));
                var keyboard=expected.GetProperty("keyboard");Near((horizontal*Math.Sin(angle)-vertical*Math.Cos(angle))*scale,Number(keyboard,"dx"),id+" accepted keyboard DX");
                Near((horizontal*Math.Cos(angle)+vertical*Math.Sin(angle))*scale,Number(keyboard,"dy"),id+" accepted keyboard DY");
            }
        }
        var guard=new OwnedMotionPresentation();var valid=new OwnedMotionAccepted(1e12,-1e12,.2,0,0,true,false,.1875);
        Require(guard.Step(Context(0),valid,0)!=null,"First accepted owned pose was missing");
        Require(guard.Step(Context(0),valid with{X=double.NaN},.016)==null&&guard.UnsupportedReason!=null,"Invalid accepted own pose reached renderer");
        Require(guard.Step(Context(0),valid,.016)?.X==valid.X,"A valid redisclosure retained invalid old display");
        Require(guard.Step(Context(0),valid,-1)==null,"Negative engine delta reached motion presentation");
        Require(guard.Step(null,null,0)==null&&guard.UnsupportedReason==null,"Owner disclosure loss retained unsupported display state");
        Require(guard.Step(Context(0),valid with{X=double.MaxValue},0)!=null,"Finite first accepted f64 origin was refused");
        Require(guard.Step(Context(0),valid with{X=-double.MaxValue},.016)==null,"Overflow in accepted origin difference reached renderer");
        Require(guard.Step(Context(0),valid with{StandingElevationM=double.MaxValue,Seated=true,SeatLift=double.MaxValue},0)==null,
            "Overflow in accepted body height reached renderer");
        Require(valid.X==1e12&&valid.LocalX==0&&valid.Heading==.2,"Owned rendering changed accepted simulation data");
        Console.WriteLine($"Source owned motion {cases} cases/{frames} frames match exact browser slices, f64 origins, capped18 motion, accepted input basis, camera/body/traversal/reset and decline guards.");
    }
}
