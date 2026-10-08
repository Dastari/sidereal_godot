using Godot;
using Sidereal.Native;

/// <summary>Display-space correction of the rendered world. Higher-layer GUI is
/// drawn afterwards and retains its theme/contrast regardless of graphics settings.</summary>
public partial class PresentationDisplay : CanvasLayer
{
    private ColorRect correction = null!;
    private ShaderMaterial material = null!;
    private NativePreferencesSnapshot? applied;

    public override void _Ready()
    {
        Layer = 1;
        material = new ShaderMaterial { Shader = new Shader { Code = """
            shader_type canvas_item;
            render_mode unshaded, blend_disabled;
            uniform sampler2D rendered_world : hint_screen_texture, repeat_disable, filter_linear;
            uniform vec4 adjustments = vec4(1.0);
            void fragment() {
                vec4 pixel = texture(rendered_world, SCREEN_UV);
                float luma = dot(pixel.rgb, vec3(0.2126, 0.7152, 0.0722));
                vec3 color = mix(vec3(luma), pixel.rgb, adjustments.w);
                color = (color * adjustments.x - 0.5) * adjustments.y + 0.5;
                color = pow(clamp(color, vec3(0.0), vec3(1.0)), vec3(1.0 / adjustments.z));
                COLOR = vec4(color, pixel.a);
            }
            """ } };
        correction = new ColorRect { Name = "WorldDisplayCorrection", Material = material,
            MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        AddChild(correction);
        correction.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    public void ApplyPreferences(NativePreferencesSnapshot preferences)
    {
        var next = preferences.Normalized();
        if (applied == next) return;
        applied = next;
        correction.Visible = next.Brightness != 1 || next.Contrast != 1 || next.Gamma != 1 || next.Saturation != 1;
        material.SetShaderParameter("adjustments", new Vector4((float)next.Brightness, (float)next.Contrast,
            (float)next.Gamma, (float)next.Saturation));
    }
}
