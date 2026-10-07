using System;
using System.Collections.Generic;

namespace Sidereal.Native.Input;

public readonly record struct MovementIntent(double Throttle, double Turn, double Dx, double Dy, bool Sprint)
{
    public static readonly MovementIntent Zero = new(0, 0, 0, 0, false);
    public bool Moving => Throttle != 0 || Turn != 0 || Dx != 0 || Dy != 0;
    public bool Finite => double.IsFinite(Throttle) && double.IsFinite(Turn) && double.IsFinite(Dx) && double.IsFinite(Dy);
}

/// <summary>Pure frontend demand math; authoritative motion is never integrated here.</summary>
public static class GameplayRules
{
    public static (double Dx, double Dy) ScreenToDeck(double horizontal, double vertical, double azimuth, double heading)
    {
        var angle = azimuth + heading;
        var scale = 1 / Math.Max(1, Math.Sqrt(horizontal * horizontal + vertical * vertical));
        return ((horizontal * Math.Sin(angle) - vertical * Math.Cos(angle)) * scale,
            (horizontal * Math.Cos(angle) + vertical * Math.Sin(angle)) * scale);
    }

    public static MovementIntent Eva(double forward, double side, double facing, bool free, bool shift)
    {
        if (!double.IsFinite(facing)) return MovementIntent.Zero;
        var turn = free && !shift ? -side : 0;
        var strafe = free && !shift ? 0 : side;
        var len = Math.Max(1, Math.Sqrt(forward * forward + strafe * strafe));
        return new(0, turn, (-Math.Sin(facing) * forward + Math.Cos(facing) * strafe) / len,
            (Math.Cos(facing) * forward + Math.Sin(facing) * strafe) / len, false);
    }

    public static double WrapAngle(double angle) => Math.Atan2(Math.Sin(angle), Math.Cos(angle));
    public static double ForwardSpeed(double heading, double vx, double vy) => -Math.Sin(heading) * vx + Math.Cos(heading) * vy;
}

/// <summary>A local convenience latch, keyed to the current accepted control relationship.</summary>
public sealed class CruiseControl
{
    private string? relationship;
    public bool Active => relationship != null;
    public double Throttle { get; private set; }
    public void Cancel() { relationship = null; Throttle = 0; }
    public bool Toggle(string? current, double forwardSpeed, double maximumSpeed = 30)
    {
        if (Active) { Cancel(); return true; }
        if (current == null || !double.IsFinite(forwardSpeed) || !double.IsFinite(maximumSpeed) || maximumSpeed <= 0) return false;
        Throttle = forwardSpeed > .1 ? Math.Min(1, forwardSpeed / maximumSpeed) : 1;
        relationship = current;
        return true;
    }
    public double Demand(double manual, string? current, bool blocked)
    {
        if (blocked || relationship != current || manual != 0) Cancel();
        return blocked ? 0 : Active ? Throttle : manual;
    }
}

/// <summary>Only keys pressed in gameplay count; clearing never resumes a physically held key.</summary>
public sealed class GameplayKeyState
{
    private readonly HashSet<string> pressed = new(StringComparer.Ordinal);
    public bool Press(string code, bool gameplayAllowed) => gameplayAllowed && pressed.Add(code);
    public void Release(string code) => pressed.Remove(code);
    public bool IsPressed(string code) => pressed.Contains(code);
    public void Clear() => pressed.Clear();
}
