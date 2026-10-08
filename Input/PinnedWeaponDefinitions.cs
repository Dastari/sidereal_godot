using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Sidereal.Bindings;

namespace Sidereal.Native.Input;

/// <summary>Exact immutable browser weapon definitions; eligibility only, all accepted state stays server-owned.</summary>
public static class PinnedWeaponDefinitions
{
    // Exact LAB_WEAPONS, packages/content/src/weapons.ts at a35632deedf210cf43f64c43cb741d841f4a1ce0.
    // Provisional authored balance. Only revision one may use this same server/browser fallback.
    // Source SHA256: 3cacae31d548d5f683cbcef8b9978face9b00c5e96d0ab7393b6ed44cf24b368
    // Canonical seed SHA256: 63d6b4c6b0cd376e140a1077796ae4442858ca6f603fccccb462dba434a5e615
    private const string RevisionOneWeaponSeed = """
{"compact-pistol":{"capacity":100,"shotCost":8,"cooldownMs":250,"rangeMeters":60,"damage":15,"reloadMs":1200},"heavy-handgun":{"capacity":100,"shotCost":16,"cooldownMs":500,"rangeMeters":60,"damage":30,"reloadMs":1400},"carbine":{"capacity":120,"shotCost":4,"cooldownMs":100,"rangeMeters":60,"damage":7,"reloadMs":1800},"long-rifle":{"capacity":120,"shotCost":24,"cooldownMs":700,"rangeMeters":60,"damage":55,"reloadMs":2200},"pistol":{"capacity":100,"shotCost":8,"cooldownMs":250,"rangeMeters":60,"damage":15,"reloadMs":1200},"smg":{"capacity":120,"shotCost":3,"cooldownMs":80,"rangeMeters":40,"damage":6,"reloadMs":1600},"compact-carbine":{"capacity":120,"shotCost":4,"cooldownMs":100,"rangeMeters":60,"damage":7,"reloadMs":1800},"rifle":{"capacity":120,"shotCost":10,"cooldownMs":250,"rangeMeters":70,"damage":20,"reloadMs":2000},"shotgun":{"capacity":100,"shotCost":20,"cooldownMs":800,"rangeMeters":18,"damage":7,"mode":"pellets","pellets":8,"spreadRad":0.35,"reloadMs":2400},"heavy-gun":{"capacity":300,"shotCost":3,"cooldownMs":60,"rangeMeters":50,"damage":8,"reloadMs":3200},"beam-rifle":{"capacity":150,"shotCost":12,"cooldownMs":300,"rangeMeters":60,"damage":24,"reloadMs":2000},"rail-rifle":{"capacity":120,"shotCost":40,"cooldownMs":1500,"rangeMeters":90,"damage":80,"reloadMs":2600},"stun-gun":{"capacity":80,"shotCost":20,"cooldownMs":900,"rangeMeters":12,"damage":5,"stunMs":2500,"reloadMs":1500},"baton":{"capacity":100,"shotCost":10,"cooldownMs":600,"rangeMeters":1.8,"damage":20,"mode":"melee","stunMs":1000},"grenade":{"capacity":100,"shotCost":50,"cooldownMs":1500,"rangeMeters":14,"damage":70,"mode":"thrown","blastRadiusM":3.5,"blastEdgeFraction":0.35,"fuseMs":1200}}
""";
    private static readonly IReadOnlyDictionary<string,string> revisionOneWeapons = JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(RevisionOneWeaponSeed)!.ToDictionary(pair=>pair.Key,pair=>pair.Value.GetRawText());
    public static (string Key,string Payload)? ResolvePayload(string definitionId,ulong revision,IEnumerable<Sidereal.Bindings.PublishedItemDefinition> rows)
    {
        if(revision==0)return null;
        var reference=$"weapon:{definitionId}@{revision}";
        var row=rows.FirstOrDefault(d=>d.DefinitionRef==reference&&d.Kind=="weapon"&&d.DefinitionId==definitionId&&d.Revision==revision&&d.Status is "published" or "retired");
        if(row!=null)return (reference+":"+row.Sha256,row.PayloadJson);
        return revision==1&&revisionOneWeapons.TryGetValue(definitionId,out var seed)?(reference+":63d6b4c6b0cd376e140a1077796ae4442858ca6f603fccccb462dba434a5e615",seed):null;
    }
    public static bool SupportsReload(string definitionId, ulong revision, IEnumerable<PublishedItemDefinition> rows)
    {
        var resolved = ResolvePayload(definitionId, revision, rows);
        if (resolved == null) return false;
        try
        {
            using var document = JsonDocument.Parse(resolved.Value.Payload);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("reloadMs", out var reload) &&
                reload.ValueKind == JsonValueKind.Number && reload.TryGetDouble(out var milliseconds) &&
                double.IsFinite(milliseconds) && milliseconds > 0;
        }
        catch (JsonException) { return false; }
    }
}
