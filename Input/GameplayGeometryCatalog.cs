using System;
using System.Linq;
using System.Text.Json;

namespace Sidereal.Native.Input;

/// <summary>Read-only interaction points exported from canonical browser geometry.
/// Private constructions must match the complete immutable renderer document.</summary>
public sealed class GameplayGeometryCatalog
{
    private readonly JsonDocument geometry;
    private readonly ReplicatedWorldCatalog? assets;
    private GameplayGeometryCatalog(string json, string? worldManifest)
    {
        geometry = JsonDocument.Parse(json);
        if (geometry.RootElement.GetProperty("schema").GetString() != "sidereal.native-gameplay-geometry.v1") throw new ArgumentException("Unsupported gameplay geometry.");
        assets = worldManifest == null ? null : new ReplicatedWorldCatalog(worldManifest);
    }
    public static GameplayGeometryCatalog Parse(string json, string? worldManifest = null) => new(json, worldManifest);
    private JsonElement? Model(ClientCore core, string shipId, bool exterior)
    {
        if (assets == null || core.Connection == null) return null;
        string? hash = null;
        if (exterior)
        {
            var description = core.Connection.Db.VisibleShipDescriptions.Iter().FirstOrDefault(row => row.ShipId == shipId);
            if (description == null || !description.PublishedExteriorAssetId.StartsWith("prefab:", StringComparison.Ordinal)) return null;
            var id = description.PublishedExteriorAssetId[7..];
            var source = assets.Root.GetProperty("ships").EnumerateArray().FirstOrDefault(row => row.GetProperty("id").GetString() == id && row.GetProperty("revision").GetUInt64() == description.AppearanceRevision);
            if (source.ValueKind != JsonValueKind.Undefined) hash = source.GetProperty("prefabSha256").GetString();
        }
        else if (core.Instance is { } instance && instance.Id == shipId)
        {
            try { hash = assets.Match(instance.DocumentJson, core.Location?.DeckId).GetProperty("prefabSha256").GetString(); }
            catch (InvalidOperationException) { return null; }
            catch (JsonException) { return null; }
        }
        if (hash == null) return null;
        var result = geometry.RootElement.GetProperty("ships").EnumerateArray().FirstOrDefault(row => row.GetProperty("prefabSha256").GetString() == hash);
        return result.ValueKind == JsonValueKind.Undefined ? null : result;
    }
    private static (double X, double Y) Point(JsonElement point) => (point[0].GetDouble(), point[1].GetDouble());
    public static bool PanelReachable(JsonElement panel, double x, double y, bool exterior)
    {
        if (panel.GetProperty("side").GetString() != (exterior ? "exterior" : "interior")) return false;
        var surface = Point(panel.GetProperty("surface")); var normal = Point(panel.GetProperty("normal")); var front = Point(panel.GetProperty("front"));
        return (x - surface.X) * normal.X + (y - surface.Y) * normal.Y > .05 && Math.Sqrt(Math.Pow(x - front.X, 2) + Math.Pow(y - front.Y, 2)) <= 1.3;
    }
    public GameplayInteraction? Resolve(ClientCore core)
    {
        var actor = core.Character; if (actor == null || core.Connection == null || !core.Alive) return null;
        var body = core.Eva; var exterior = body != null;
        if (exterior && (body!.Phase != "local" || body.AnchorShipId.Length == 0 || !core.SpatialReady)) return null;
        var shipId = exterior ? body!.AnchorShipId : core.Instance?.Id;
        if (shipId == null || Model(core, shipId, exterior) is not { } model) return null;
        double x = actor.LocalX, y = actor.LocalY;
        if (exterior)
        {
            var pose = core.Connection.Db.VisibleShipMotion.Iter().FirstOrDefault(row => row.ShipId == shipId && row.SystemId == body!.SystemId);
            if (pose == null) return null;
            var dx = body!.X - pose.X; var dy = body.Y - pose.Y;
            x = Math.Cos(pose.Heading) * dx + Math.Sin(pose.Heading) * dy;
            y = -Math.Sin(pose.Heading) * dx + Math.Cos(pose.Heading) * dy;
        }
        var logic = model.GetProperty("logic");
        if (logic.ValueKind != JsonValueKind.Null)
        {
            var panels = logic.GetProperty("panels").EnumerateArray().Where(panel => PanelReachable(panel, x, y, exterior));
            var panel = panels.OrderBy(p => { var f = Point(p.GetProperty("front")); return Math.Pow(x - f.X, 2) + Math.Pow(y - f.Y, 2); }).FirstOrDefault();
            if (panel.ValueKind != JsonValueKind.Undefined)
            {
                var id = panel.GetProperty("deviceId").GetString()!;
                if (exterior && !core.Connection.Db.VisibleShipLogic.Iter().Any(row => row.ShipId == shipId && row.DeviceId == id && row.Kind == "button")) return null;
                string? port = null;
                if (logic.GetProperty("graph").GetProperty("wires").TryGetProperty(id + ".pressed", out var targets) && targets.GetArrayLength() > 0) port = targets[0].GetProperty("port").GetString();
                var label = port switch { "cycle" => "Cycle airlock", "open_outer" => "Open airlock (outer door)", "open_inner" => "Open airlock (inner door)", _ => "Press button" };
                return new("ship-button", id, label, true, ShipId: shipId);
            }
        }
        if (!exterior) return null;
        var doors = logic.ValueKind == JsonValueKind.Null ? Array.Empty<string>() : logic.GetProperty("doors").EnumerateArray().Select(row => row.GetProperty("doorId").GetString()!).ToArray();
        var entry = model.GetProperty("eva").GetProperty("entries").EnumerateArray().Where(row => !doors.Contains(row.GetProperty("id").GetString())).Select(row => {
            var outside = Point(row.GetProperty("outside")); return (Entry: row, Distance: Math.Sqrt(Math.Pow(x - outside.X, 2) + Math.Pow(y - outside.Y, 2)));
        }).Where(row => row.Distance <= 2.5).OrderBy(row => row.Distance).FirstOrDefault();
        return entry.Entry.ValueKind == JsonValueKind.Undefined ? null : new("airlock-entry", entry.Entry.GetProperty("id").GetString()!, "Enter the airlock", true, ShipId: shipId);
    }
}
