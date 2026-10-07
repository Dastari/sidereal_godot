using Godot;
using System;

namespace Sidereal.Native;

public enum SourceLightClass { Hull, Interior, Crew, ReferenceSurface, Weather }

/// <summary>Receiver layers and color/unit conversion; never changes imported art colors.</summary>
public static class SourceLightUnits
{
    public const uint LocalCaster = 1u << 3;
    public const uint Hull = 1u << 4, Interior = 1u << 5, Crew = 1u << 6,
        ReferenceSurface = 1u << 7, Weather = 1u << 8;
    public const int DirectionalLimit = 8;
    public static uint CameraMask(uint visibility) => visibility | LocalCaster;
    public static uint Cohort(SourceLightClass source) => source switch
    {
        SourceLightClass.Hull => Hull, SourceLightClass.Interior => Interior,
        SourceLightClass.Crew => Crew, SourceLightClass.ReferenceSurface => ReferenceSurface,
        SourceLightClass.Weather => Weather, _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
    public static double Direct(SourceLightClass source) => source switch
    {
        SourceLightClass.Hull => SourceLightUnitsRules.HullDirect,
        SourceLightClass.Interior => SourceLightUnitsRules.InteriorDirect,
        SourceLightClass.Weather => SourceLightUnitsRules.WeatherDirect,
        SourceLightClass.Crew => SourceLightUnitsRules.CrewDirect,
        SourceLightClass.ReferenceSurface => SourceLightUnitsRules.ReferenceDirect,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
    public static float Energy(double intensity, double direct = 1) => SourceLightUnitsRules.Energy(intensity, direct);
    public static Color Colour(Color sourceLinear) => sourceLinear.LinearToSrgb();
    public static Color ManualHexColour(string hex)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(hex,@"^#[0-9a-fA-F]{6}$"))
            throw new FormatException("Source manual appearance colors require six-digit RGB hex.");
        var color = Color.FromString(hex, Colors.White);
        return new Color((float)SourceLightUnitsRules.ManualHexChannelToLinear(color.R),
            (float)SourceLightUnitsRules.ManualHexChannelToLinear(color.G),
            (float)SourceLightUnitsRules.ManualHexChannelToLinear(color.B), color.A).LinearToSrgb();
    }
    public static void SetReceiver(GeometryInstance3D geometry, SourceLightClass source, uint visibility, bool localCaster = true)
    {
        geometry.Layers = visibility | Cohort(source) | (localCaster ? LocalCaster : 0);
        geometry.SetMeta("source_light_class", source.ToString());
        geometry.SetMeta("source_direct_intensity", Direct(source));
    }
    public static void Apply(Light3D light, double intensity, Color sourceLinear, double direct, uint receiverMask, uint visibility)
    {
        light.LightEnergy = Energy(intensity, direct);
        light.LightColor = Colour(sourceLinear);
        light.LightCullMask = receiverMask;
        light.ShadowCasterMask = LocalCaster;
        light.Layers = visibility;
    }
}
