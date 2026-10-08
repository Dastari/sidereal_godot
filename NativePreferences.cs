using System;
using System.IO;
using System.Text.Json;

namespace Sidereal.Native;

/// <summary>Device presentation preferences. No actor, token, authority or gameplay data.</summary>
public sealed record NativePreferencesSnapshot
{
    public double Brightness { get; init; } = 1;
    public double Contrast { get; init; } = 1;
    public double Gamma { get; init; } = 1;
    public double Saturation { get; init; } = 1;
    public double PanelOpacity { get; init; } = .94;
    public double UiScale { get; init; } = 1;
    public bool Fullscreen { get; init; }
    public bool ReducedMotion { get; init; }
    public bool ShowControlHints { get; init; } = true;
    public string VistaId { get; init; } = "deep-space";
    public string LocalLightLimit { get; init; } = "all";
    public string Antialiasing { get; init; } = "msaa";
    public int MsaaSamples { get; init; } = 4;
    public double RenderScale { get; init; } = 1;
    public bool Lighting { get; init; } = true;
    public bool Shadows { get; init; } = true;
    public bool Glow { get; init; } = true;

    public NativePreferencesSnapshot Normalized() => this with
    {
        Brightness = Finite(Brightness, .5, 1.5, 1), Contrast = Finite(Contrast, .5, 1.5, 1),
        Gamma = Finite(Gamma, .5, 2, 1), Saturation = Finite(Saturation, 0, 2, 1),
        PanelOpacity = Finite(PanelOpacity, .3, 1, .94), UiScale = Finite(UiScale, .75, 1.5, 1),
        RenderScale = Finite(RenderScale, .5, 1, 1),
        LocalLightLimit = LocalLightLimit is "0" or "4" or "8" or "16" or "32" or "all" ? LocalLightLimit : "all",
        Antialiasing = Antialiasing is "off" or "msaa" ? Antialiasing : "msaa",
        MsaaSamples = MsaaSamples is 2 or 4 or 8 ? MsaaSamples : 4,
        VistaId = VistaId is { Length: > 0 and <= 64 } && System.Text.RegularExpressions.Regex.IsMatch(VistaId, "^[a-z0-9-]+$") ? VistaId : "deep-space"
    };
    private static double Finite(double value, double minimum, double maximum, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    /// <summary>Browser display-space correction, after scene tone mapping; never applied to UI.</summary>
    public double[] TransferDisplay(double red, double green, double blue)
    {
        var p = Normalized(); var luma = red * .2126 + green * .7152 + blue * .0722;
        double Channel(double value) => Math.Pow(Math.Clamp(((luma + (value - luma) * p.Saturation) * p.Brightness - .5) * p.Contrast + .5, 0, 1), 1 / p.Gamma);
        return new[] { Channel(red), Channel(green), Channel(blue) };
    }
}

public sealed class NativePreferences
{
    public static NativePreferences Current { get; } = new();
    public NativePreferencesSnapshot Snapshot { get; private set; } = new();
    public event Action<NativePreferencesSnapshot>? Changed;
    public bool Loaded { get; private set; }
    public string? PersistenceError { get; private set; }
    private string? path;
    private sealed record Profile(int Version, NativePreferencesSnapshot Presentation);

    public bool Load(string profilePath)
    {
        path = profilePath; Loaded = false; PersistenceError = null; Snapshot = new();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            if (info.Length > 65536) { PersistenceError = "The display profile was too large and has been reset."; return false; }
            var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path));
            if (profile is not { Version: 1, Presentation: not null }) return false;
            Snapshot = profile.Presentation.Normalized(); Loaded = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { PersistenceError = "Display preferences could not be read. Defaults are active."; }
        return Loaded;
    }
    public void Set(NativePreferencesSnapshot next)
    {
        var normalized = next.Normalized(); if (normalized == Snapshot) return;
        Snapshot = normalized; Save(); Changed?.Invoke(Snapshot);
    }
    public bool Save()
    {
        if (path == null) return false;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Profile(1, Snapshot), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true); PersistenceError = null; return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { PersistenceError = "Display changes apply for this session but could not be saved."; return false; }
    }
    public void ResetGraphics() => Set(Snapshot with { Brightness = 1, Contrast = 1, Gamma = 1, Saturation = 1,
        LocalLightLimit = "all", Antialiasing = "msaa", MsaaSamples = 4, RenderScale = 1, Lighting = true, Shadows = true, Glow = true });
    public void ResetDisplay() => Set(Snapshot with { PanelOpacity = .94, UiScale = 1, Fullscreen = false, ReducedMotion = false, ShowControlHints = true, VistaId = "deep-space" });
}
