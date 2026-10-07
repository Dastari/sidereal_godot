using Godot;
using System;
using System.Text.Json;
namespace Sidereal.Native;

/// <summary>Pinned browser surface-family values. Authored colours and texture bytes stay intact.</summary>
public static class SourceSurfaceFinish
{
    private static JsonElement Source => SpaceEnvironment.Catalog.GetProperty("surfaceFinish");
    public static string? ShipFamily(string slot) => Source.GetProperty("ships").TryGetProperty(slot,out var value)?value.GetString():null;
    public static string? CrewFamily(string slot,bool body) => Source.GetProperty("crew").GetProperty(body?"body":"armour").TryGetProperty(slot,out var value)?value.GetString():null;
    public static void Apply(BaseMaterial3D material,string? family)
    {
        if(family==null||!Source.GetProperty("families").TryGetProperty(family,out var finish))return;
        float N(string key)=>finish.GetProperty(key).GetSingle();
        material.Metallic=N("metallic");material.Roughness=N("roughness");
        // Pinned browser default keeps this optional lobe disabled (review-only ?coat=1).
        // Retain the authored coefficients for a future explicit, qualified opt-in.
        material.ClearcoatEnabled=false;material.Clearcoat=N("coat");material.ClearcoatRoughness=N("coatRoughness");
        // Godot uses F0=.16*specular^2 for dielectrics; Babylon uses the authored IOR and multiplier.
        var ior=N("ior");var f0=Math.Pow((ior-1)/(ior+1),2)*N("specular");material.MetallicSpecular=(float)Math.Clamp(Math.Sqrt(f0/.16),0,1);
        material.SetMeta("source_surface_family",family);material.SetMeta("source_environment_intensity",N("environment"));
    }
}
