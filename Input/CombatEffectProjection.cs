using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Sidereal.Native.Input;

public readonly record struct CombatPoint3(double X, double Y, double Z)
{
    public bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public CombatPoint3 Normalized => Length > 1e-12 ? this * (1 / Length) : new(0, 0, -1);
    public static CombatPoint3 operator +(CombatPoint3 a, CombatPoint3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static CombatPoint3 operator -(CombatPoint3 a, CombatPoint3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static CombatPoint3 operator *(CombatPoint3 a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
    public static CombatPoint3 Lerp(CombatPoint3 a, CombatPoint3 b, double weight) => a + (b - a) * weight;
}

public sealed record CombatFxSample(CombatPoint3 Scale, double Opacity, double Emissive, bool Finished);
public sealed class CombatFxDefinition
{
    private readonly JsonElement source;
    public string Id { get; }
    public string File { get; }
    public string Kind { get; }
    public string Tint { get; }
    public bool Loop { get; }
    public double Duration { get; }
    public double? Speed { get; }
    public double? Length { get; }
    public CombatFxDefinition(JsonElement source)
    {
        this.source = source.Clone();
        Id = source.GetProperty("id").GetString()!; File = source.GetProperty("file").GetString()!;
        Kind = source.GetProperty("kind").GetString()!; Tint = source.GetProperty("tint").GetString()!;
        Loop = source.GetProperty("loop").GetBoolean(); Duration = source.GetProperty("durationS").GetDouble();
        Speed = Number(source, "speedMps"); Length = Number(source, "lengthM");
        if (!(Duration > 0) || !double.IsFinite(Duration) || source.GetProperty("keys").GetArrayLength() < 2)
            throw new ArgumentException("Invalid accepted combat FX duration/keys.");
    }
    private static double? Number(JsonElement source, string key) => source.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    public CombatFxSample Sample(double seconds)
    {
        var unit = Loop ? ((seconds / Duration % 1) + 1) % 1 : Math.Clamp(seconds / Duration, 0, 1);
        var keys = source.GetProperty("keys"); var index = 0;
        while (index < keys.GetArrayLength() - 2 && keys[index + 1].GetProperty("t").GetDouble() < unit) index++;
        var a = keys[index]; var b = keys[Math.Min(index + 1, keys.GetArrayLength() - 1)];
        var at = a.GetProperty("t").GetDouble(); var bt = b.GetProperty("t").GetDouble();
        var weight = bt > at ? Math.Clamp((unit - at) / (bt - at), 0, 1) : 0;
        double Lerp(double x, double y) => x + (y - x) * weight;
        double Scale(int axis) => Lerp(a.GetProperty("scale")[axis].GetDouble(), b.GetProperty("scale")[axis].GetDouble());
        return new(new(Scale(0), Scale(1), Scale(2)), Lerp(a.GetProperty("opacity").GetDouble(), b.GetProperty("opacity").GetDouble()),
            Lerp(a.GetProperty("emissive").GetDouble(), b.GetProperty("emissive").GetDouble()), !Loop && seconds >= Duration);
    }
}

public sealed class CombatEffectCatalog
{
    public const string SourceRevision = "a35632deedf210cf43f64c43cb741d841f4a1ce0";
    private readonly Dictionary<string, CombatFxDefinition> effects;
    private readonly Dictionary<string, JsonElement> items;
    public CombatEffectCatalog(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        if (root.GetProperty("schema").GetString() != "sidereal.native-combat-fx.v1" || root.GetProperty("sourceCommit").GetString() != SourceRevision)
            throw new ArgumentException("Unsupported combat effect source pin.");
        effects = root.GetProperty("fx").EnumerateArray().Select(row => new CombatFxDefinition(row)).ToDictionary(row => row.Id, StringComparer.Ordinal);
        items = root.GetProperty("items").EnumerateArray().ToDictionary(row => row.GetProperty("id").GetString()!, row => row.Clone(), StringComparer.Ordinal);
    }
    public IEnumerable<CombatFxDefinition> Effects => effects.Values;
    public CombatFxDefinition? Effect(string id) => effects.GetValueOrDefault(id);
    public bool HasItem(string? id) => id != null && items.ContainsKey(id);
    public string? ItemFx(string? item, string trigger) => item != null && items.TryGetValue(item, out var row) &&
        row.GetProperty("fx").TryGetProperty(trigger, out var fx) ? fx.GetString() : null;
    public string? ThrownFile(string? item) => item != null && items.TryGetValue(item, out var row) && row.TryGetProperty("thrownFile", out var file) ? file.GetString() : null;
    public JsonElement? ItemMaterials(string? item) => item != null && items.TryGetValue(item, out var row) ? row.GetProperty("materials") : null;
    public CombatPoint3? Tint(string? item, string fx)
    {
        if (item == null || !items.TryGetValue(item, out var row) || Effect(fx) is not { Tint: not "fixed" } definition ||
            !row.GetProperty("materials").TryGetProperty(definition.Tint, out var slot)) return null;
        var color = slot.TryGetProperty("emissive", out var emission) ? emission : slot.GetProperty("color");
        return new(color[0].GetDouble(), color[1].GetDouble(), color[2].GetDouble());
    }
}

/// <summary>The source FX observer shows spawn once, then caps each render step at 100ms.</summary>
public sealed class CombatFxTimeline
{
    private readonly CombatFxDefinition definition;
    private readonly double size, lengthScale;
    private readonly double? travelLength, hold;
    private bool shown;
    public double Time { get; private set; }
    public double TravelFraction { get; private set; }
    public CombatFxTimeline(CombatFxDefinition definition, double size = 1, double? length = null, double? travelLength = null, double? hold = null)
    { this.definition = definition; this.size = size; lengthScale = definition.Length.HasValue && length.HasValue ? length.Value / definition.Length.Value : 1; this.travelLength = travelLength; this.hold = hold; }
    public CombatFxSample Step(double delta)
    {
        if (shown) Time += Math.Clamp(delta, 0, .1); shown = true;
        var finished = false;
        if (travelLength.HasValue)
        {
            var distance = Math.Min(travelLength.Value, Time * (definition.Speed ?? 60));
            TravelFraction = travelLength.Value > 0 ? distance / travelLength.Value : 1;
            finished = distance >= travelLength.Value;
        }
        var sample = definition.Sample(travelLength.HasValue ? Math.Min(Time, definition.Duration * .5) : Time);
        if (!travelLength.HasValue) finished = definition.Loop ? Time >= (hold ?? definition.Duration) : sample.Finished;
        return new(new(sample.Scale.X * size, sample.Scale.Z * size, sample.Scale.Y * lengthScale * size), sample.Opacity, sample.Emissive, finished);
    }
}

public sealed record CombatActionInput(string CharacterId, string ShipId, string DeckId, string? ItemId, string Mode, ulong ShotSequence,
    double OriginX, double OriginY, string PointsJson, double LandX, double LandY, bool Detonated, double BlastRadius,
    ulong ReloadSequence, ulong StunSequence);
public sealed record CombatRay(CombatPoint3 End, bool Struck);
public sealed record CombatShotProjection(CombatPoint3 Origin, CombatPoint3 Direction, IReadOnlyList<CombatRay> Rays);
public enum CombatVisualEventKind { Shot, Reload, Stun, Detonation }
public sealed record CombatVisualEvent(CombatVisualEventKind Kind, CombatActionInput Action);
public sealed record CombatEffectChanges(IReadOnlyList<CombatVisualEvent> Events, IReadOnlyList<string> RemovedActors);

public sealed class CombatActionObserver
{
    private sealed record Seen(ulong Shot, ulong Reload, ulong Stun, bool Detonated);
    private readonly Dictionary<string, Seen> seen = new(StringComparer.Ordinal);
    public void Clear() => seen.Clear();
    public CombatEffectChanges Observe(IEnumerable<CombatActionInput> rows)
    {
        var events = new List<CombatVisualEvent>(); var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            keep.Add(row.CharacterId);
            if (!seen.TryGetValue(row.CharacterId, out var previous))
            { seen[row.CharacterId] = new(row.ShotSequence, row.ReloadSequence, row.StunSequence, row.Detonated); continue; }
            var detonated = previous.Detonated;
            if (row.ShotSequence != previous.Shot)
            {
                detonated = row.Detonated;
                if (row.ShotSequence > 0) events.Add(new(CombatVisualEventKind.Shot, row));
            }
            if (row.ReloadSequence != previous.Reload) events.Add(new(CombatVisualEventKind.Reload, row));
            if (row.StunSequence != previous.Stun) events.Add(new(CombatVisualEventKind.Stun, row));
            if (row.Mode == "thrown" && row.Detonated && !detonated)
            { detonated = true; events.Add(new(CombatVisualEventKind.Detonation, row)); }
            seen[row.CharacterId] = new(row.ShotSequence, row.ReloadSequence, row.StunSequence, detonated);
        }
        var removed = seen.Keys.Where(id => !keep.Contains(id)).ToArray();
        foreach (var id in removed) seen.Remove(id);
        return new(events, removed);
    }
}

public static class CombatEffectProjection
{
    public const double BeamHeight = 1.3;
    public const double LegacyImpactHeight = 3d / 16 + BeamHeight;
    public const double TracerLifetime = .110;
    public const double ImpactLifetime = .350;
    public static IReadOnlyList<(double X, double Y, bool Struck)> ParsePoints(string json)
    {
        var points = new List<(double, double, bool)>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return points;
            foreach (var point in document.RootElement.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 3 ||
                    point.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))) continue;
                points.Add((point[0].GetDouble(), point[1].GetDouble(), point[2].GetDouble() == 1));
                if (points.Count == 32) break;
            }
        }
        catch (JsonException) { }
        return points;
    }
    public static CombatShotProjection Shot(CombatActionInput action, double standingHeight, CombatPoint3? muzzle = null, CombatPoint3? direction = null)
    {
        var origin = muzzle ?? new CombatPoint3(action.OriginX, standingHeight + BeamHeight, -action.OriginY);
        var rays = ParsePoints(action.PointsJson).Select(point => new CombatRay(new(point.X, origin.Y, -point.Y), point.Struck)).ToArray();
        return new(origin, direction?.Normalized ?? (rays.Length > 0 ? (rays[0].End - origin).Normalized : new(0, 0, -1)), rays);
    }
    public static CombatPoint3 WorldRenderPoint(double worldX, double worldY, double height, double originX, double originY) => new(worldX - originX, height, -(worldY - originY));
    public static CombatPoint3 WorldToShip(double worldX, double worldY, double height, double shipX, double shipY, double heading)
    {
        var x = worldX - shipX; var y = worldY - shipY;
        return new(Math.Cos(heading) * x + Math.Sin(heading) * y, height, Math.Sin(heading) * x - Math.Cos(heading) * y);
    }
    public static (CombatPoint3 Axis, double Angle) ForwardRotation(CombatPoint3 direction)
    {
        var normal = direction.Normalized;
        var axis = new CombatPoint3(normal.Y, -normal.X, 0);
        var dot = -normal.Z;
        return axis.Length * axis.Length < 1e-12
            ? (new(0, 1, 0), dot > 0 ? 0 : Math.PI)
            : (axis.Normalized, Math.Acos(Math.Clamp(dot, -1, 1)));
    }
    public static (double Scale, double Opacity, bool Finished) LegacyImpact(double age)
    { var unit = age / ImpactLifetime; return (.12 + unit * .35, Math.Max(0, 1 - unit), unit >= 1); }
    public static CombatPoint3 SparkVelocity(int index)
    { var angle = index / 6d * Math.PI * 2; return new(Math.Cos(angle) * 1.6, .8 + index % 3 * .5, Math.Sin(angle) * 1.6); }
    public static CombatPoint3 ThrownPosition(CombatPoint3 from, CombatPoint3 to, double seconds)
    {
        var length = (to - from).Length; var duration = Math.Clamp(length / 11, .25, 1.1);
        var unit = Math.Min(1, Math.Max(0, seconds / duration)); var arc = Math.Min(1.2, .3 + length * .1);
        var point = CombatPoint3.Lerp(from, to, unit); return point with { Y = point.Y + 4 * arc * unit * (1 - unit) };
    }
}
