using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using Sidereal.Native;

namespace Sidereal.InventoryUi;

internal static class InventoryIcons
{
    private static Dictionary<string, string>? definitions, urls, demoAliases;
    private static readonly Dictionary<string, Texture2D?> textures = new();
    private static void Load()
    {
        if (definitions != null) return;
        definitions = new(); urls = new(); demoAliases = new();
        using var document = JsonDocument.Parse(FileAccess.GetFileAsString("res://InventoryUi/Icons/manifest.json"));
        foreach (var row in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            var path = row.GetProperty("resource").GetString()!;
            definitions[row.GetProperty("definitionId").GetString()!] = path;
            var url = row.GetProperty("iconUrl").GetString();
            if (!string.IsNullOrEmpty(url)) urls[url] = path;
        }
        foreach (var pair in document.RootElement.GetProperty("demoAliases").EnumerateObject()) demoAliases[pair.Name] = pair.Value.GetString()!;
    }
    public static Texture2D? Texture(ItemDefinition? definition)
    {
        if (definition == null) return null;
        Load();
        string? path = null;
        if (definition.IconUrl.Length > 0) urls!.TryGetValue(definition.IconUrl, out path);
        if (path == null && definition.Revision == 1)
        {
            var id = demoAliases!.TryGetValue(definition.Id, out var demoId) ? demoId : definition.Id;
            definitions!.TryGetValue(id, out path);
        }
        if (path == null) return null;
        if (!textures.TryGetValue(path, out var texture)) textures[path] = texture = ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
        return texture;
    }
    public static string PresentationId(string id)
    {Load();return demoAliases!.TryGetValue(id,out var alias)?alias:id;}
    public static Rect2 Fit(Texture2D texture, Rect2 bounds)
    {
        var size = texture.GetSize();
        var scale = Math.Min(bounds.Size.X / size.X, bounds.Size.Y / size.Y);
        var fitted = size * scale;
        return new Rect2(bounds.GetCenter() - fitted * .5f, fitted);
    }
    public static int QuarterTurns(ItemDefinition definition, Texture2D texture, bool rotated)
    {
        // Existing handheld renders can be landscape while a legacy pin keeps its portrait footprint.
        var source = texture.GetSize();
        var baseTurn = (definition.Height > definition.Width && source.X > source.Y * 1.1f) ||
            (definition.Width > definition.Height && source.Y > source.X * 1.1f) ? 1 : 0;
        return baseTurn + (rotated ? 1 : 0);
    }
}
