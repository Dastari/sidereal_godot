using System;

namespace Sidereal.Native;

public enum SourceDisplaySpaceProfile { Unqualified, Vulkan, CompatibilityMaterial, CompatibilityPost }

public readonly record struct SourceDisplaySpaceContext(
    string Renderer, string Driver, bool EnginePinned, bool Filmic,
    double Exposure, double White, double RenderScale, bool Glow,
    bool Ssao = false, bool NativeAdjustments = false, bool CanvasBackground = false,
    bool Hdr2D = false, bool CameraEffects = false, bool Fog = false,
    bool TransparentBackground = false, bool SupportedScaler = true);

public readonly record struct SourceDisplaySpaceDecision(SourceDisplaySpaceProfile Profile, string Status)
{
    public bool Qualified => Profile != SourceDisplaySpaceProfile.Unqualified;
}

/// <summary>Inverse of pinned Godot4.7.2's SDR output for the source's RGBA8 sky.
/// These values serve only the sky shader, never imported materials or global tone.</summary>
public static class SourceDisplaySpaceRules
{
    public const double Exposure = .97, White = 1;
    public const int Codes = 256;

    public static SourceDisplaySpaceDecision Evaluate(SourceDisplaySpaceContext context)
    {
        string? reason = !context.EnginePinned ? "engine is not pinned Godot4.7.2" :
            !context.Filmic || !Near(context.Exposure, Exposure) || !Near(context.White, White) ? "changed Filmic/exposure/white" :
            !double.IsFinite(context.RenderScale) || context.RenderScale < .5 || context.RenderScale > 1 || !context.SupportedScaler ? "unqualified 3D scaling" :
            context.Ssao ? "SSAO" : context.NativeAdjustments ? "native color adjustments" :
            context.CanvasBackground ? "canvas background" : context.Hdr2D ? "HDR2D/output" :
            context.CameraEffects ? "camera exposure/compositor" : context.Fog ? "fog" :
            context.TransparentBackground ? "transparent background" : null;
        if (reason != null) return new(SourceDisplaySpaceProfile.Unqualified, "sky display unqualified: " + reason);
        if (context.Renderer == "forward_plus" && context.Driver == "vulkan")
            return new(SourceDisplaySpaceProfile.Vulkan, "sky SDR matched: Vulkan Filmic inverse");
        if (context.Renderer == "gl_compatibility" && context.Driver == "opengl3")
        {
            // RendererViewport snaps a spatial scale within Godot's EPSILON
            // of1 to an unscaled buffer before GLES chooses its tone path.
            var nativeScale = (float)context.RenderScale;
            var post = context.Glow || nativeScale < 1f - .0001f || nativeScale > 1f + .0001f;
            return new(post ? SourceDisplaySpaceProfile.CompatibilityPost : SourceDisplaySpaceProfile.CompatibilityMaterial,
                post ? "sky SDR matched: OpenGL post inverse" : "sky SDR matched: OpenGL material inverse");
        }
        return new(SourceDisplaySpaceProfile.Unqualified, "sky display unqualified: renderer/driver");
    }

    private static bool Near(double a, double b) => double.IsFinite(a) && Math.Abs(a - b) < 1e-6;

    public static float NativeInput(int code, SourceDisplaySpaceProfile profile)
    {
        if (code < 0 || code >= Codes) throw new ArgumentOutOfRangeException(nameof(code));
        if (profile == SourceDisplaySpaceProfile.Unqualified || !Enum.IsDefined(profile))
            throw new ArgumentOutOfRangeException(nameof(profile));
        var encoded = code / 255d;
        var compatibility = profile != SourceDisplaySpaceProfile.Vulkan;
        // GLES uses the pinned approximate encoder even below the sRGB knee.
        var output = compatibility ? InverseGlesEncode(encoded) :
            encoded < .04045 ? encoded / 12.92 : Math.Pow((encoded + .055) / 1.055, 2.4);
        var input = InverseFilmic(output);
        // GLES moves tone into post for glow/upscaling and writes an additional
        // encoded LDR intermediate. It does not apply the tone curve twice.
        if (profile == SourceDisplaySpaceProfile.CompatibilityPost)
            input = InverseGlesEncode(InverseGlesDecode(input));
        input /= Exposure;
        return (float)(compatibility ? InverseGlesDecode(input) : input);
    }

    public static float[] Lookup(SourceDisplaySpaceProfile profile)
    {
        var values = new float[Codes];
        for (var code = 0; code < values.Length; code++) values[code] = NativeInput(code, profile);
        return values;
    }

    private static double FilmicRaw(double x) =>
        (x * (.88 * x + .06) + .002) / (x * (.88 * x + .6) + .06) - .01 / .3;
    private static double InverseFilmic(double y)
    {
        var k = y * FilmicRaw(White) + .01 / .3;
        var a = .88 * (1 - k); var b = .6 * (.1 - k); var c = .2 * (.01 - .3 * k);
        return Math.Max(0, (-b + Math.Sqrt(Math.Max(0, b * b - 4 * a * c))) / (2 * a));
    }
    private static double InverseGlesEncode(double x) => x <= 0 ? 0 : Math.Pow((x + .055) / 1.055, 2.4);
    private static double GlesDecode(double x) => x * (x * (x * .305306011 + .682171111) + .012522878);
    private static double InverseGlesDecode(double y)
    {
        double low = 0, high = 2;
        for (var i = 0; i < 32; i++)
        {
            var middle = (low + high) * .5;
            if (GlesDecode(middle) < y) low = middle; else high = middle;
        }
        return (low + high) * .5;
    }
}
