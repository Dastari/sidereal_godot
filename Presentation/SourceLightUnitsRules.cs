using System;

namespace Sidereal.Native;

/// <summary>Scalar adapter for the pinned Babylon 9.25.0 and Godot 4.7.2 light paths.</summary>
public static class SourceLightUnitsRules
{
    public const double HullDirect = .6, InteriorDirect = .9, CrewDirect = 1, ReferenceDirect = 1, WeatherDirect = 2.4;

    public static float Energy(double sourceIntensity, double materialDirect)
    {
        if (!double.IsFinite(sourceIntensity) || !double.IsFinite(materialDirect) || sourceIntensity < 0 || materialDirect < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceIntensity), "Source light values must be finite and nonnegative.");
        var energy = sourceIntensity * materialDirect / Math.PI;
        if (!double.IsFinite(energy) || energy > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(sourceIntensity), "Source light energy exceeds the native range.");
        return (float)energy;
    }

    // Babylon's pinned Color3.toLinearSpace() defaults to the power-2.2 path.
    // glTF factors are already linear and must never pass through this method.
    public static double ManualHexChannelToLinear(double channel)
    {
        if (!double.IsFinite(channel) || channel < 0 || channel > 1)
            throw new ArgumentOutOfRangeException(nameof(channel));
        return Math.Pow(channel, 2.2);
    }

    public static bool IsInteriorPaletteSlot(string role, string slot) => role switch
    {
        "floor" => slot is "primary" or "secondary" or "trim" or "dark" or "metal",
        "wall" => slot is "primary" or "secondary" or "trim",
        _ => false,
    };

    public static double ShipDirect(bool authoredPalette, string role, string slot) =>
        !authoredPalette && IsInteriorPaletteSlot(role, slot) ? InteriorDirect : HullDirect;
}
