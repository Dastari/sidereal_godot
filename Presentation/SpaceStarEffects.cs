using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Sidereal.Native;

/// <summary>Browser stellar convection and rare optically thin eruptions on the unchanged native star.</summary>
public static class SpaceStarEffects
{
    private static Shader? convection,corona;
    public static void Attach(Node3D root,uint layers,bool glow)
    {
        convection??=new Shader{Code=Convection};corona??=new Shader{Code=Corona};
        foreach(var mesh in Meshes(root))
        for(var i=0;i<(mesh.Mesh?.GetSurfaceCount()??0);i++)
        {
            if(mesh.GetActiveMaterial(i) is not BaseMaterial3D source)continue;
            var adapted=new ShaderMaterial{Shader=convection};adapted.SetShaderParameter("albedo",source.AlbedoColor);adapted.SetShaderParameter("authored_emission",source.Emission);adapted.SetShaderParameter("emission_energy",source.EmissionEnergyMultiplier*.75f/.97f);adapted.SetShaderParameter("roughness",source.Roughness);adapted.SetShaderParameter("metallic",source.Metallic);mesh.SetSurfaceOverrideMaterial(i,adapted);mesh.ExtraCullMargin=.05f;
        }
        var plasma=new MeshInstance3D{Name="ExactRareStellarCorona",Mesh=new QuadMesh{Size=new Vector2(4.3f,4.3f)},Layers=layers,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off,MaterialOverride=new ShaderMaterial{Shader=corona},Visible=glow};plasma.SetMeta("celestial_halo",true);root.AddChild(plasma);
    }
    public static void Update(Node3D root,double time)
    {
        var (angle,progress,strength)=SpaceMath.StellarEruption(time);
        foreach(var mesh in Meshes(root)) {
            if(mesh.MaterialOverride is ShaderMaterial plasma&&mesh.HasMeta("celestial_halo")){plasma.SetShaderParameter("stellar_time",(float)time);plasma.SetShaderParameter("eruption",new Vector4((float)angle,(float)progress,(float)strength,0));}
            for(var i=0;i<(mesh.Mesh?.GetSurfaceCount()??0);i++)if(mesh.GetSurfaceOverrideMaterial(i) is ShaderMaterial material)material.SetShaderParameter("stellar_time",(float)time);
        }
    }
    private static IEnumerable<MeshInstance3D> Meshes(Node n){if(n is MeshInstance3D mesh)yield return mesh;foreach(var c in n.GetChildren())foreach(var child in Meshes(c))yield return child;}
    private const string Convection="""
shader_type spatial;render_mode cull_back;
uniform vec4 albedo:source_color;uniform vec4 authored_emission:source_color;uniform float emission_energy;uniform float roughness;uniform float metallic;uniform float stellar_time;
varying vec3 stellar_position;varying vec3 stellar_radial;
float stellar_spots(vec3 p,float t){float result=0.;for(int i=0;i<7;i++){float f=float(i);float a=f*2.399963+.11*sin(t*.11+f*1.7);float z=-.72+1.44*(f+.5)/7.+.07*sin(t*.14+f*2.1);vec3 center=vec3(sqrt(1.-z*z)*cos(a),sqrt(1.-z*z)*sin(a),z);float radius=.14+.045*sin(t*.16+f*1.3);float edge=length(p-center)+.012*sin(p.x*63.+t*.5)*sin(p.z*51.-t*.4);result=max(result,1.-smoothstep(radius*.55,radius,edge));}return result;}
vec3 stellar_tile(vec2 coord){float a=(coord.x-.5)*6.28318530718;float b=(1.-coord.y)*3.14159265359;return vec3(sin(b)*cos(a),sin(b)*sin(a),cos(b));}
void vertex(){vec3 tile=stellar_tile(UV);float wave=sin(tile.x*24.+stellar_time*.85)*sin(tile.y*21.-stellar_time*.72)*sin(tile.z*19.+stellar_time*.67);float lift=.022*wave-.025*stellar_spots(tile,stellar_time);VERTEX+=vec3(tile.x,tile.z,-tile.y)*lift;stellar_position=tile;stellar_radial=(MODELVIEW_MATRIX*vec4(VERTEX,0.)).xyz;}
void fragment(){vec3 sp=normalize(stellar_position);float st=stellar_time;vec3 drift=vec3(sin(sp.y*7.+st*.85),sin(sp.z*9.-st*.72),sin(sp.x*8.+st*.67));vec3 cell=sp*16.+drift*2.2;vec3 river=sp*8.+drift;float flow=sin(river.x+sin(river.z*.7)+st*.18)+sin(river.y+sin(river.x*.6)-st*.16)+.55*sin(river.z*1.3);float channel=1.-smoothstep(.12,.5,abs(flow));float granule=sin(sp.x*63.+st*.8)*sin(sp.y*59.-st*.67)*sin(sp.z*61.+st*.53);float thermal=sin(cell.x+sin(cell.y*1.7-st*.8))*sin(cell.y+sin(cell.z*1.9+st*.7))*sin(cell.z+sin(cell.x*1.3-st*.6));float boil=smoothstep(.25,.72,thermal);vec3 emission=authored_emission.rgb*emission_energy;emission*=vec3(1.,.62,.22)*(.50+.12*granule+boil*.85);emission+=vec3(1.,.28,.006)*pow(boil,3.)*.30;emission*=.5+.5*channel;emission+=vec3(4.2,1.3,.035)*channel*channel*smoothstep(-.35,.5,sin(sp.z*7.+st*.3)+thermal*.4)*(.24+.76*smoothstep(-.4,.6,thermal));float cool=stellar_spots(sp,st);emission=mix(emission,vec3(.035,.004,.0002),cool);float limb=pow(1.-abs(dot(normalize(stellar_radial),VIEW)),10.);emission+=vec3(1.,.66,.11)*limb*(4.+3.*sin(sp.y*9.+sp.x*7.)+channel*2.);ALBEDO=albedo.rgb*(1.-.98*cool);METALLIC=metallic;ROUGHNESS=roughness;EMISSION=emission;}
""";
    private const string Corona="""
shader_type spatial;render_mode unshaded,cull_disabled,blend_add,depth_draw_never;
uniform float stellar_time;uniform vec4 eruption;
float hash(vec2 p){return fract(sin(dot(p,vec2(127.1,311.7)))*43758.5453);}
float noise(vec2 p){vec2 i=floor(p),f=fract(p);f=f*f*(3.-2.*f);return mix(mix(hash(i),hash(i+vec2(1,0)),f.x),mix(hash(i+vec2(0,1)),hash(i+vec2(1,1)),f.x),f.y);}
float turbulence(vec2 p){return .57*noise(p)+.28*noise(p*2.03)+.15*noise(p*4.07);}
void fragment(){float time=stellar_time;vec2 p=(UV-.5)*4.3;float r=length(p);float a=atan(p.y,p.x);vec2 direction=p/max(r,.001);float warp=turbulence(direction*7.+vec2(time*.12,-time*.09));float flow=turbulence(direction*19.+vec2(r*15.-time*.9,warp*4.));float reach=1.20+.62*warp+.24*flow;float base=smoothstep(.90,.985,r);float outer=base*(1.-smoothstep(1.02,reach,r));float hot=base*exp(-pow(abs((r-.995)/(.035+.026*flow)),2.));float wisp=base*exp(-max(r-1.015,0.)*(4.5-1.5*warp))*(.25+.75*flow);float fade=1.-smoothstep(1.85,2.1,r);vec3 orange=vec3(1.,.205,.002);vec3 c=(orange*(outer*1.5+wisp*1.1)+vec3(1.,.65,.10)*hot*(1.3+flow))*fade;float da=atan(sin(a-eruption.x),cos(a-eruption.x));float age=eruption.y,strength=eruption.z;float height=.35+.70*smoothstep(0.,.72,age);float h=(r-.97)/height;float bend=.42*sin(clamp(h,0.,1.)*3.8-age*1.6)*smoothstep(0.,.45,age)*smoothstep(0.,.35,h);float curtain=turbulence(vec2(da*15.+time*.34,h*6.-time*1.7)+warp*2.);float width=(.23+.10*curtain)*(1.-.65*smoothstep(.25,1.,h));float spread=exp(-pow(abs((da-bend)/width),2.));float radial=smoothstep(.93,1.015,r)*(1.-smoothstep(.80+.15*curtain,1.08,h));float ragged=smoothstep(.15,.73,curtain);float strands=.5+.5*sin(h*24.+curtain*7.-time*2.);float veil=.12+.60*ragged*(.35+.65*strands);float plume=spread*radial*strength*veil;float root_heat=plume*exp(-max(h,0.)*3.8);c+=orange*(plume*3.2+root_heat*.6+plume*pow(strands,4.)*1.5);ALBEDO=c;ALPHA=clamp(base*(outer+hot+wisp)*fade+plume,0.,1.);}
""";
}
