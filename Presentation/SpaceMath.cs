using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
namespace Sidereal.Native;
/// <summary>Exact numerical browser presentation rules; no simulation writes.</summary>
public static class SpaceMath
{
    public const double FieldOfView = .5;
    public static readonly double DeckBeta = Math.Acos(1 / Math.Sqrt(3));
    public readonly record struct Star(double X, double Y, double Z, double AngularRadius, double Red, double Green, double Blue);
    public static IReadOnlyList<Star> Stars(int count = 8192, uint seed = 117)
    {
        if (count < 1 || count > 16384) throw new ArgumentOutOfRangeException(nameof(count));
        var state = seed; double Random() { state = unchecked(state * 1664525 + 1013904223); return state / 4294967296d; }
        var phase = Random() * Math.PI * 2;
        return Enumerable.Range(0, count).Select(i => {
            var y = 1 - 2 * (i + .25 + Random() * .5) / count; var r = Math.Sqrt(Math.Max(0, 1 - y * y));
            var a = phase + i * Math.PI * (3 - Math.Sqrt(5)) + (Random() - .5) * .3;
            var b = .45 + Math.Pow(Random(), 2) * .95; var warm = Random();
            return new Star(r * Math.Cos(a), y, r * Math.Sin(a), .00045 + Math.Pow(Random(), 3) * .0011, b * (.76 + warm * .24), b * .88, b * (1 - warm * .18));
        }).ToArray();
    }
    public static int PlanetLod(double px, int prior = 2) => prior == 0 && px > 150 || px > 190 ? 0 : prior <= 1 && px > 36 || px > 50 ? 1 : 2;
    public static int ShipLod(double px, int prior = 2) => px >= (prior == 0 ? 44 : 56) ? 0 : px >= (prior == 2 ? 7 : 5) ? 1 : 2;
    public static double ProjectedRadius(double r, double distance, double height) => r > 0 && double.IsFinite(r + distance + height) ? r * height / (Math.Max(r, distance) * 2 * Math.Tan(FieldOfView / 2)) : 0;
    public static double AngleDelta(double from, double to) => Math.Atan2(Math.Sin(to - from), Math.Cos(to - from));
    public static double Ease(double current, double target, double dt, double speed = 12) => current + (target - current) * (1 - Math.Exp(-speed * Math.Max(0, dt)));
    public static double DeckActorWeight(double half, double overview = 12) { var t = Math.Clamp((half - 2) / (Math.Max(3, overview) - 2), 0, 1); return 1 - .4 * t * t * (3 - 2 * t); }
    public static (double Width, double Length) ExhaustShape(string kind, double throttle) { var t = Math.Clamp(throttle, 0, 1); return kind switch { "rcs" => (.9 + .5 * t, 3 + 12 * t), "reverser" => (.55 + .25 * t, 1.5 + 4 * t), _ => (.8 + .35 * Math.Sqrt(t), 2 + 11 * t) }; }
    public static (double Angle,double Progress,double Strength) StellarEruption(double time){var elapsed=time-4;var eventNumber=Math.Floor(elapsed/18);var age=elapsed-eventNumber*18;var progress=eventNumber>=0&&age<6?age/6:0;return((.9+Math.Max(0,eventNumber)*2.3999632297%(Math.PI*2))%(Math.PI*2),progress,Math.Pow(Math.Sin(Math.PI*progress),2));}
    public readonly record struct Dust(double X, double Y, double Height, double Size, double LengthVariation);
    public readonly record struct DustGrid(double Spacing, int Columns, int Rows, int Count);
    public readonly record struct DustResponse(double Speed, double Length, double StreakRatio, double WarpBlend, double Intensity, double Heading);
    public readonly record struct Point3(double X,double Y,double Z);
    public readonly record struct DustDepth(double Height,double CenterX,double CenterZ,double DepthDistance,double HalfX,double HalfZ,double Thickness,double SizeScale);
    public static DustDepth[] DustDepthLayers(Point3 camera,Point3 target,double fov,double aspect)
    {
        var dx=target.X-camera.X;var dy=target.Y-camera.Y;var dz=target.Z-camera.Z;var distance=Math.Sqrt(dx*dx+dy*dy+dz*dz);
        if(!double.IsFinite(distance)||distance<1||dy>=-.1)return Array.Empty<DustDepth>();
        var fx=dx/distance;var fy=dy/distance;var fz=dz/distance;var lateral=Math.Sqrt(fx*fx+fz*fz);var rx=lateral>.0001?-fz/lateral:1;var rz=lateral>.0001?fx/lateral:0;
        var ux=-rz*fy;var uy=rz*fx-rx*fz;var uz=rx*fy;var level=Math.Max(8,Math.Pow(2,Math.Floor(Math.Log2(-dy))));var heightStep=level/4;var nearHeight=Math.Floor((camera.Y-level*.18)/heightStep)*heightStep;var tangent=Math.Tan(fov/2);
        return new[]{0d,.6,1.8}.Select((depth,index)=>{var height=nearHeight-depth*level;var depthDistance=(height-camera.Y)/fy;var cx=camera.X+fx*depthDistance;var cz=camera.Z+fz*depthDistance;var halfX=0d;var halfZ=0d;
            foreach(var sx in new[]{-1,1})foreach(var sy in new[]{-1,1}){var rayX=fx+rx*sx*tangent*aspect+ux*sy*tangent;var rayY=fy+uy*sy*tangent;var rayZ=fz+rz*sx*tangent*aspect+uz*sy*tangent;var travel=(height-camera.Y)/Math.Min(-.05,rayY);halfX=Math.Max(halfX,Math.Abs(camera.X+rayX*travel-cx));halfZ=Math.Max(halfZ,Math.Abs(camera.Z+rayZ*travel-cz));}
            return new DustDepth(height,cx,cz,depthDistance,halfX*1.1,halfZ*1.1,level*.035,new[]{1.35,1,.75}[index]*.42);}).ToArray();
    }
    public static DustGrid DustLayout(double half, double aspect, int budget = 576)
    {
        half = double.IsFinite(half) ? Math.Clamp(half, 8, 4000) : 55; aspect = double.IsFinite(aspect) ? Math.Clamp(aspect, .25, 4) : 1;
        var count = Math.Clamp(budget, 16, 576); var columns = (int)Math.Floor(Math.Sqrt(count) * Math.Sqrt(aspect) + .5); var rows = count / columns;
        var needed = Math.Max((half * aspect * 2 + 32) / columns, (half * 2 + 32) / rows);
        return new DustGrid(Math.Max(8, Math.Pow(2, Math.Ceiling(Math.Log2(needed)))), columns, rows, columns * rows);
    }
    private static double CellHash(double x, double y, int salt)
    {
        var h = unchecked((int)(x % 2147483647) * 73856093 ^ (int)(y % 2147483647) * 19349663 ^ salt * 83492791);
        h = unchecked((h ^ (int)((uint)h >> 13)) * 1274126177); return (uint)(h ^ (int)((uint)h >> 16)) / 4294967295d;
    }
    public static Dust DustCell(int i, double x, double y, double spacing, int columns = 24, int rows = 24)
    {
        var ox = i % columns - columns / 2; var oy = i / columns - rows / 2; var cx = Math.Floor(x / spacing); var cy = Math.Floor(y / spacing); var a = cx + ox; var b = cy + oy;
        return new Dust((ox + CellHash(a, b, 1)) * spacing - (x - cx * spacing), (oy + CellHash(a, b, 2)) * spacing - (y - cy * spacing), -12 - CellHash(a, b, 3) * 42, (.07 + CellHash(a, b, 4) * .08) * spacing * 2.4 / 8, .75 + CellHash(a, b, 5) * .4);
    }
    public static DustResponse DustMotion(double vx, double vy, bool reduced)
    {
        var speed = double.IsFinite(vx) && double.IsFinite(vy) ? Math.Sqrt(vx * vx + vy * vy) : 0; var trail = reduced ? 0 : Math.Clamp((speed - 40) / 120, 0, 1); var fast = reduced ? 0 : Math.Clamp((speed - 600) / 2400, 0, 1);
        var warp = fast * fast * (3 - 2 * fast); var ratio = 1 + Math.Floor((trail * 2 + warp * 45) * 4) / 4;
        return new DustResponse(speed, .16 * ratio, ratio, warp, .8 + trail * .12 + warp * .45, speed > 0 ? Math.Atan2(vx, -vy) : 0);
    }
    private static double N(JsonElement f, string key, double fallback = 0) => f.TryGetProperty(key, out var v) && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : fallback;
    private static string T(JsonElement f, string key, string fallback = "") => f.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
    private static double Smooth(double d, double feather) { var t = feather > 0 ? Math.Clamp(d / feather, 0, 1) : d >= 0 ? 1 : 0; return t * t * (3 - 2 * t); }
    public static double FieldInterior(JsonElement f, double wx, double wy, double height)
    {
        var x = wx - N(f, "x"); var y = wy - N(f, "y"); var z = height - N(f, "height"); var w = N(f, "width"); var l = N(f, "length"); var d = N(f, "depth");
        if (w <= 0 || l <= 0 || d <= 0) return double.NegativeInfinity;
        var shape = T(f, "shape"); if (shape == "ellipsoid") return (1 - Math.Sqrt(Math.Pow(x / (w / 2), 2) + Math.Pow(y / (l / 2), 2) + Math.Pow(z / (d / 2), 2))) * Math.Min(w, Math.Min(l, d)) / 2;
        var dz = d / 2 - Math.Abs(z); if (shape == "box") return Math.Min(w / 2 - Math.Abs(x), Math.Min(l / 2 - Math.Abs(y), dz));
        if (!f.TryGetProperty("vertices", out var polygon) || polygon.GetArrayLength() < 3) return double.NegativeInfinity;
        var points = polygon.EnumerateArray().ToArray(); var inside = false; var distance = double.PositiveInfinity;
        for (var i = 0; i < points.Length; i++) { var a = points[i]; var b = points[(i + 1) % points.Length]; var ax = N(a,"x"); var ay = N(a,"y"); var bx = N(b,"x"); var by = N(b,"y"); var dx = bx - ax; var dy = by - ay; var s = dx * dx + dy * dy; var t = s > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / s, 0, 1) : 0; distance = Math.Min(distance, Math.Sqrt(Math.Pow(x - ax - t * dx,2) + Math.Pow(y - ay - t * dy,2))); if ((ay > y) != (by > y) && x < dx * (y - ay) / dy + ax) inside = !inside; }
        return Math.Min(inside ? distance : -distance, dz);
    }
    public static IReadOnlyDictionary<string,double> BackgroundWeights(string? json, double x, double y, double height = 0)
    {
        var weights = new Dictionary<string,double> { ["deep-space"] = 1 }; if (string.IsNullOrWhiteSpace(json) || json.Length > 4_194_304) return weights;
        try {
            using var parsed = JsonDocument.Parse(json); var r = parsed.RootElement; var c = r.GetProperty("center"); var radius = N(r,"radius"); if (radius <= 0) return weights;
            var distance = radius - Math.Sqrt(Math.Pow(x - N(c,"x"),2) + Math.Pow(y - N(c,"y"),2) + Math.Pow(height - N(c,"height"),2)); var system = Smooth(distance,N(r,"feather",Math.Min(radius * .05,100000)));
            void Overlay(string id,double alpha) { if (id.Length == 0 || alpha <= 0) return; foreach(var key in weights.Keys.ToArray())weights[key] *= 1 - alpha;weights[id] = weights.GetValueOrDefault(id) + alpha; }
            Overlay(T(r,"backgroundId","deep-space"),system); if (system <= 0) return weights;
            var hasZones = r.TryGetProperty("zones",out var zones) && zones.ValueKind == JsonValueKind.Array;
            var allZones = hasZones ? zones.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
            var fields = hasZones ? allZones.Where(f => !f.TryGetProperty("root",out var root) || !root.GetBoolean()).ToArray() : r.GetProperty("fields").EnumerateArray().ToArray(); var byId = allZones.ToDictionary(f => T(f,"id"));
            foreach(var f in fields.Where(f => T(f,"backgroundId").Length > 0).OrderBy(f => N(f,"level")).ThenBy(f => N(f,"priority")).ThenBy(f => T(f,"id"),StringComparer.Ordinal)) {
                var clipped = system; if(f.TryGetProperty("ancestors",out var ancestors))foreach(var a in ancestors.EnumerateArray())if(byId.TryGetValue(a.GetString()!,out var p) && (!p.TryGetProperty("root",out var root) || !root.GetBoolean()))clipped *= Smooth(FieldInterior(p,x,y,height),N(p,"feather",Math.Min(N(p,"width"),Math.Min(N(p,"length"),N(p,"depth"))) * .1));
                Overlay(T(f,"backgroundId"),clipped * Smooth(FieldInterior(f,x,y,height),N(f,"feather",Math.Min(N(f,"width"),Math.Min(N(f,"length"),N(f,"depth"))) * .1)));
            }
        } catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { return new Dictionary<string,double> { ["deep-space"] = 1 }; }
        return weights.Where(w => w.Value > 0).ToDictionary(w => w.Key,w => w.Value);
    }
}
