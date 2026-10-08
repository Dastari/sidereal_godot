using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Sidereal.Native;

/// <summary>Four source key responses and three molded rims, within both tested eight-light caps.</summary>
public partial class SourceLightUnitsRig : Node3D
{
    private readonly List<(DirectionalLight3D Light, double Direct, bool Rim)> lights = new();
    private uint visibility = 1;
    public SourceLightUnitsRig() { }
    public SourceLightUnitsRig(uint visibilityLayer) { visibility = visibilityLayer; }
    public object Facts => new {
        nodes = lights.Count, active = lights.Count(l => l.Light.IsVisibleInTree()),
        limit = SourceLightUnits.DirectionalLimit, casterMask = SourceLightUnits.LocalCaster,
        lights = lights.Select(l => new { name = l.Light.Name.ToString(), l.Direct, l.Rim,
            energy = l.Light.LightEnergy, receiverMask = l.Light.LightCullMask,
            casterMask = l.Light.ShadowCasterMask, shadows = l.Light.ShadowEnabled }).ToArray(),
    };
    public override void _Ready()
    {
        Name = "SourceMaterialLightResponses";
        Add("StellarHull", SourceLightUnits.Hull, SourceLightUnitsRules.HullDirect, false);
        Add("StellarInterior", SourceLightUnits.Interior, SourceLightUnitsRules.InteriorDirect, false);
        Add("StellarCrewAndReference", SourceLightUnits.Crew | SourceLightUnits.ReferenceSurface, 1, false);
        Add("StellarCloudWeather", SourceLightUnits.Weather, SourceLightUnitsRules.WeatherDirect, false);
        Add("MoldedHullRim", SourceLightUnits.Hull, SourceLightUnitsRules.HullDirect, true);
        Add("MoldedInteriorRim", SourceLightUnits.Interior, SourceLightUnitsRules.InteriorDirect, true);
        Add("MoldedCrewRim", SourceLightUnits.Crew, 1, true);
        Sync(new Vector3(-.6f,-1,.45f), Vector3.Forward, true, true, Colors.White);
    }
    private void Add(string name, uint receivers, double direct, bool rim)
    {
        var light = new DirectionalLight3D { Name = name, DirectionalShadowMaxDistance = 160 };
        SourceLightUnits.Apply(light, rim ? .8 : 2.1, rim ? new Color(.86f,.92f,1) : Colors.White,
            direct, receivers, visibility);
        AddChild(light); lights.Add((light,direct,rim));
    }
    public void Sync(Vector3 keyDirection, Vector3 cameraForward, bool enabled, bool shadows,
        Color sourceKeyColour, double sourceKeyIntensity = 2.1)
    {
        cameraForward.Y = 0;
        if (cameraForward.LengthSquared() < 1e-6) cameraForward = Vector3.Forward;
        cameraForward = cameraForward.Normalized();
        var rimDirection = new Vector3(-cameraForward.X,-.55f,-cameraForward.Z);
        foreach (var entry in lights)
        {
            var light = entry.Light;
            light.Visible = enabled;
            light.ShadowEnabled = shadows && !entry.Rim && light.LightCullMask != SourceLightUnits.Weather;
            light.LightEnergy = SourceLightUnits.Energy(entry.Rim ? .8 : sourceKeyIntensity, entry.Direct);
            light.LightColor = SourceLightUnits.Colour(entry.Rim ? new Color(.86f,.92f,1) : sourceKeyColour);
            var direction = entry.Rim ? rimDirection : keyDirection;
            if (IsInsideTree() && direction.IsFinite() && direction.LengthSquared() > 1e-8)
            {
                var magnitude=Math.Sqrt((double)direction.X*direction.X+(double)direction.Y*direction.Y+(double)direction.Z*direction.Z);
                var normal=new Vector3((float)(direction.X/magnitude),(float)(direction.Y/magnitude),(float)(direction.Z/magnitude));
                // A valid light direction can be vertical; LookAt requires an
                // independent up vector even though its rotation around the ray
                // has no effect on the directional source energy.
                var up=Math.Abs(normal.Dot(Vector3.Up))>.999 ? Vector3.Forward : Vector3.Up;
                light.LookAt(light.GlobalPosition + normal, up);
            }
        }
    }
}
