using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Public authored asset data only; never a replica of private world rows.</summary>
public sealed class ReplicatedWorldCatalog
{
    private readonly JsonDocument document;
    public JsonElement Root => document.RootElement;
    public ReplicatedWorldCatalog(string json)
    {
        document = JsonDocument.Parse(json);
        if (Root.GetProperty("schema").GetString() != "sidereal.native-world-assets.v1")
            throw new InvalidOperationException("Unsupported native world asset catalog.");
    }

    public JsonElement Preview(string prefabId) => Root.GetProperty("ships").EnumerateArray()
        .First(ship => ship.GetProperty("id").GetString() == prefabId);

    // No id-only match: a custom live ship with the same template id is different geometry.
    public JsonElement Match(string documentJson, string? deckId)
    {
        using var source = JsonDocument.Parse(documentJson);
        if (!source.RootElement.TryGetProperty("prefab", out var binding) ||
            !binding.TryGetProperty("document", out var prefab) ||
            !binding.TryGetProperty("catalog", out var catalog))
            throw new InvalidOperationException("This construction has no bundled authored prefab renderer.");
        var digest = CanonicalSha256(prefab);
        var restoredConstruction = ConstructionSha256(source.RootElement);
        var decks = source.RootElement.GetProperty("layout").GetProperty("decks");
        if (decks.GetArrayLength() != 1 || prefab.GetProperty("decks").GetArrayLength() != 1)
            throw new InvalidOperationException("Multiple deck presentation requires an updated native renderer.");
        foreach (var ship in Root.GetProperty("ships").EnumerateArray())
        {
            if (ship.GetProperty("catalog").GetString() != catalog.GetString() ||
                ship.GetProperty("prefabSha256").GetString() != digest) continue;
            if (restoredConstruction != ConstructionSha256(ship.GetProperty("document")))
                throw new InvalidOperationException("This construction was modified. Its exact authored presentation is not bundled.");
            if (deckId != null && !decks.EnumerateArray()
                .Any(deck => deck.GetProperty("id").GetString() == deckId))
                throw new InvalidOperationException("The active deck is absent from this replicated construction.");
            return ship;
        }
        throw new InvalidOperationException("This exact ship revision is not bundled. Update the native asset catalog.");
    }

    // Mirror the backend's restorePrefabIdentities: only spawn IDs and the source
    // provenance envelope are transient. Every floor, wall/opening, room and visual
    // field still has to match the complete canonical public construction document.
    private static JsonElement RestoreConstructionIdentities(JsonElement source)
    {
        var root = JsonNode.Parse(source.GetRawText())!.AsObject();
        var prefab = root["prefab"]!.AsObject();
        if (prefab["identities"] is not { } identityNode) return source;
        var back = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var identity in identityNode.AsObject())
        {
            var instance = identity.Value?.GetValue<string>();
            if (string.IsNullOrEmpty(instance) || !back.TryAdd(instance, identity.Key))
                throw new InvalidOperationException("Invalid construction identity map.");
        }
        string Restore(string? id) => id != null && back.TryGetValue(id, out var original) ? original :
            throw new InvalidOperationException("Construction identity is absent from its source binding.");
        void Field(JsonNode node, string field) => node[field] = Restore(node[field]?.GetValue<string>());
        void References(JsonNode node, string field)
        {
            if (node[field] is not JsonArray references) return;
            for (var index = 0; index < references.Count; index++) references[index] = Restore(references[index]?.GetValue<string>());
        }
        prefab.Remove("identities");
        var layout = root["layout"]!;
        if (layout["source"] == null) throw new InvalidOperationException("Unspawned construction has an instance identity map.");
        layout["source"] = null;
        Field(layout, "id"); Field(layout, "playableDeckId");
        foreach (var deck in layout["decks"]!.AsArray())
        {
            Field(deck!, "id");
            foreach (var hole in deck!["holes"]!.AsArray()) Field(hole!, "id");
        }
        foreach (var collection in new[] { "tiles", "partitions", "openings", "rooms" })
        foreach (var row in layout[collection]!.AsArray())
        {
            Field(row!, "id"); Field(row!, "deckId");
            if (collection == "openings") Field(row!, "partitionId");
            if (collection == "rooms") { References(row!, "boundaryIds"); References(row!, "tileIds"); }
        }
        foreach (var floor in root["floors"]!.AsArray()) { Field(floor!, "id"); Field(floor!, "deckId"); }
        return JsonSerializer.SerializeToElement(root);
    }

    public static string ConstructionSha256(JsonElement source)
    {
        var root = JsonNode.Parse(RestoreConstructionIdentities(source).GetRawText())!;
        var layout = root["layout"]!;
        void Sort(JsonNode parent, string field)
        {
            if (parent[field] is not JsonArray values) return;
            parent[field] = new JsonArray(values.OrderBy(value => value?["id"]?.GetValue<string>(), StringComparer.Ordinal)
                .ThenBy(value => value?["revision"]?.ToJsonString(), StringComparer.Ordinal).Select(value => value?.DeepClone()).ToArray());
        }
        foreach (var field in new[] { "decks", "tiles", "partitions", "openings", "rooms", "fittings", "routes", "nodes", "dependencies", "serviceConnections" })
            Sort(layout, field);
        Sort(root, "floors");
        foreach (var tile in layout["tiles"]!.AsArray())
        {
            var vertices = tile!["vertices"]!.AsArray().Select(point => new[] { point![0]!.GetValue<double>(), point[1]!.GetValue<double>() }).ToList();
            var area = vertices.Select((point, index) => point[0] * vertices[(index + 1) % vertices.Count][1] - vertices[(index + 1) % vertices.Count][0] * point[1]).Sum();
            if (area < 0) vertices.Reverse();
            var start = Enumerable.Range(0, vertices.Count).OrderBy(index => vertices[index][0]).ThenBy(index => vertices[index][1]).First();
            tile["vertices"] = new JsonArray(Enumerable.Range(0, vertices.Count)
                .Select(index => JsonSerializer.SerializeToNode(vertices[(start + index) % vertices.Count].Select(axis => axis == 0 ? 0 : axis).ToArray())).ToArray());
        }
        return CanonicalSha256(JsonSerializer.SerializeToElement(root));
    }

    public static string CanonicalSha256(JsonElement value)
    {
        using var bytes = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteCanonical(writer, value);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray(); break;
            case JsonValueKind.Number: writer.WriteNumberValue(value.GetDouble()); break;
            default: value.WriteTo(writer); break;
        }
    }

    public static IReadOnlyDictionary<string, FurnishingPose> ReadFurnishings(string json)
    {
        if (json.Length > 16384) throw new InvalidOperationException("Furnishing state exceeds its supported budget.");
        using var source = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (source.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Invalid furnishing state.");
        return source.RootElement.EnumerateObject().ToDictionary(property => property.Name, property =>
        {
            var value = property.Value;
            var x = value.GetProperty("dx").GetDouble(); var y = value.GetProperty("dy").GetDouble();
            var yaw = value.GetProperty("yaw").GetDouble();
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(yaw))
                throw new InvalidOperationException("Invalid furnishing transform.");
            return new FurnishingPose(x, y, yaw, value.GetProperty("deleted").GetBoolean());
        });
    }

    public static void ValidateFurnishings(JsonElement ship, IReadOnlyDictionary<string, FurnishingPose> furnishings)
    {
        var movable = ship.GetProperty("placements").EnumerateArray()
            .Where(placement => placement.GetProperty("movable").GetBoolean())
            .Select(placement => placement.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
        if (furnishings.Keys.Any(id => !movable.Contains(id)))
            throw new InvalidOperationException("The replicated furnishing overlay needs an updated native asset catalog.");
    }
}

public readonly record struct FurnishingPose(double X, double Y, double Yaw, bool Deleted);
