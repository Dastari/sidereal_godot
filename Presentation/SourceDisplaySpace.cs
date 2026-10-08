using Godot;
using System;
using System.Collections.Generic;

namespace Sidereal.Native;

/// <summary>Bounded display-space adapter for the pinned sky. Unsupported output
/// pipelines retain the native sky path and expose an unqualified status.</summary>
public sealed class SourceDisplaySpace : IDisposable
{
    private readonly Dictionary<SourceDisplaySpaceProfile, ImageTexture> lookups = new();
    private SourceDisplaySpaceDecision? applied;
    public string Status { get; private set; } = "sky display awaiting camera";
    public SourceDisplaySpaceProfile Profile { get; private set; }

    public void Apply(ShaderMaterial material, Camera3D camera, Viewport viewport)
    {
        var environment = camera.Environment;
        var version = Engine.GetVersionInfo();
        var attributes = camera.Attributes;
        var context = new SourceDisplaySpaceContext(
            RenderingServer.GetCurrentRenderingMethod(), RenderingServer.GetCurrentRenderingDriverName(),
            version["major"].AsInt32() == 4 && version["minor"].AsInt32() == 7 && version["patch"].AsInt32() == 2,
            environment?.TonemapMode == Godot.Environment.ToneMapper.Filmic,
            environment?.TonemapExposure ?? double.NaN, environment?.TonemapWhite ?? double.NaN,
            viewport.Scaling3DScale, environment?.GlowEnabled == true,
            environment?.SsaoEnabled == true, environment?.AdjustmentEnabled == true,
            environment?.BackgroundMode == Godot.Environment.BGMode.Canvas, viewport.UseHdr2D,
            camera.Compositor != null || attributes?.AutoExposureEnabled == true ||
                attributes is CameraAttributesPhysical || attributes != null && Math.Abs(attributes.ExposureMultiplier - 1) > 1e-6,
            environment?.FogEnabled == true || environment?.VolumetricFogEnabled == true,
            viewport.TransparentBg, viewport.Scaling3DMode == Viewport.Scaling3DModeEnum.Bilinear);
        var decision = SourceDisplaySpaceRules.Evaluate(context);
        Status = decision.Status; Profile = decision.Profile;
        if (applied == decision) return;
        applied = decision;
        material.SetShaderParameter("source_display_enabled", decision.Qualified);
        if (!decision.Qualified) return;
        if (!lookups.TryGetValue(decision.Profile, out var lookup))
        {
            var values = SourceDisplaySpaceRules.Lookup(decision.Profile);
            var bytes = new byte[values.Length * sizeof(float)]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            using var image = Image.CreateFromData(SourceDisplaySpaceRules.Codes, 1, false, Image.Format.Rf, bytes);
            lookup = ImageTexture.CreateFromImage(image); lookups[decision.Profile] = lookup;
        }
        material.SetShaderParameter("source_display_lut", lookup);
    }

    public void Dispose()
    {
        foreach (var lookup in lookups.Values) lookup.Dispose();
        lookups.Clear(); applied = null;
    }

    public const string Shader = """
uniform bool source_display_enabled = false;
uniform sampler2D source_display_lut:filter_nearest,repeat_disable;
vec3 source_display_adapter(vec3 encoded){
    if(!source_display_enabled)return encoded;
    vec3 code=floor(clamp(encoded,vec3(0.),vec3(1.))*255.+.5);
    return vec3(texture(source_display_lut,vec2((code.r+.5)/256.,.5)).r,
        texture(source_display_lut,vec2((code.g+.5)/256.,.5)).r,
        texture(source_display_lut,vec2((code.b+.5)/256.,.5)).r);
}
""";
}
