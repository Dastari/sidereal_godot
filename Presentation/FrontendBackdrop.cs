using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Local entry presentation. The dock reuses published metre-scale modules; the
/// berth displays the same authored Wayfarer as the replicated game renderer.
/// This scene neither publishes a blueprint nor changes authoritative world state.
/// </summary>
public partial class FrontendBackdrop : Node3D
{
    [Export] public Vector3 PresentationCameraPosition { get; set; } = new(24, 14, 18);
    [Export] public Vector3 PresentationCameraTarget { get; set; } = new(-8, 2.8f, -12);
    [Export(PropertyHint.Range, "35,80,1")] public float PresentationFieldOfView { get; set; } = 44;
    [Export] public bool DockedShipCutaway { get; set; } = true;
    private const uint PresentationLayer = 1u << 19;
    private const string Assets = "res://Assets/Frontend/";
    private readonly Dictionary<string, PackedScene> scenes = new();
    private Camera3D camera = null!;
    private bool active = true;

    public override void _Ready()
    {
        Name = "FrontendBackdrop";
        BuildConcourse();
        BuildDockedAssembly();
        BuildCargo();
        BuildLighting();
        // The existing sky plate is only the distant view through the aperture.
        // Every nearby deck, wall, beam, ship fitting and cargo object is 3D geometry.
        AddChild(new MeshInstance3D
        {
            Name = "OrionVista", Position = new Vector3(-45, -6, -70),
            Rotation = new Vector3(0, 0.52f, 0), Layers = PresentationLayer,
            Mesh = new QuadMesh { Size = new Vector2(180, 120) },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoTexture = GD.Load<Texture2D>(Assets + "orion-veil-v1.png"),
                AlbedoColor = new Color(0.3f, 0.34f, 0.46f),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        });
        camera = new Camera3D
        {
            Name = "PresentationCamera", Position = PresentationCameraPosition,
            Fov = PresentationFieldOfView, Near = 0.1f, Far = 200,
            CullMask = PresentationLayer, Environment = CreateEnvironment(),
        };
        AddChild(camera);
        camera.LookAt(PresentationCameraTarget, Vector3.Up);
        SetVisible(active);
    }

    /// <summary>Main restores its gameplay camera after hiding this presentation.</summary>
    public new void SetVisible(bool visible)
    {
        active = visible;
        Visible = visible;
        if (camera != null) camera.Current = visible;
    }

    private Godot.Environment CreateEnvironment() => new()
    {
        BackgroundMode = Godot.Environment.BGMode.Sky,
        Sky = new Sky
        {
            SkyMaterial = new PanoramaSkyMaterial
            {
                Panorama = GD.Load<Texture2D>(Assets + "orion-veil-v1.png"),
                Filter = true, EnergyMultiplier = 0.3f,
            },
        },
        SkyRotation = new Vector3(0, 0.8f, 0),
        AmbientLightSource = Godot.Environment.AmbientSource.Color,
        AmbientLightColor = new Color("536680"), AmbientLightEnergy = 0.18f,
        ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
        TonemapMode = Godot.Environment.ToneMapper.Filmic,
    };

    private void BuildConcourse()
    {
        // Installed deck panels are two metres wide; corridor modules are one metre.
        // Their original geometry and texture mapping remain at exported unit scale.
        var floor = new List<Transform3D>();
        var aisle = new List<Transform3D>();
        for (var x = -26; x < 26; x += 2)
        for (var z = -40; z <= 14; z += 2)
        {
            if (x == -14)
            {
                aisle.Add(At(x, 0, z)); aisle.Add(At(x + 1, 0, z));
                aisle.Add(At(x, 0, z - 1)); aisle.Add(At(x + 1, 0, z - 1));
            }
            else floor.Add(At(x, 0, z));
        }
        // A local dock finish keeps the light deck texture from overwhelming the
        // hero ship. Materials are copied; published resources and ship art are untouched.
        AddBatch("native-deck-kit.glb", floor, "GEO-part-a3f5c1c3caa171a94d6c--floor",
            new Color(0.3f, 0.36f, 0.46f));
        AddBatch("int.floor.corridor.glb", aisle);

        var panels = new List<Transform3D>();
        var braces = new List<Transform3D>();
        var lamps = new List<Transform3D>();
        for (var z = -40; z < 14; z++)
        {
            panels.Add(At(-26, 0, z, Mathf.Pi / 2));
            braces.Add(At(26, 0, z - 1, -Mathf.Pi / 2));
            if (z % 4 == 0)
            {
                lamps.Add(At(-25.7f, 1.7f, z - 0.31f, Mathf.Pi / 2));
                lamps.Add(At(25.7f, 1.7f, z - 0.69f, -Mathf.Pi / 2));
            }
        }
        AddBatch("int.edge.panel.glb", panels);
        AddBatch("int.edge.reinforced.glb", braces);

        // Repeated native posts provide aperture depth and a service gantry.
        // Slight overlaps follow their existing 2.3125 m exported height.
        var posts = new List<Transform3D>();
        var overhead = new List<Transform3D>();
        foreach (var z in new[] { -38f, -22f })
        {
            for (var height = 0; height < 6; height++)
            {
                posts.Add(At(-26, height * 2.125f, z));
                posts.Add(At(25.625f, height * 2.125f, z));
            }
            for (var x = -26f; x < 26; x += 2.125f)
                overhead.Add(new Transform3D(new Basis(Vector3.Forward, Mathf.Pi / 2),
                    new Vector3(x + 2.125f, 12.75f, z)));
            foreach (var x in new[] { -20f, -12f, -4f, 4f, 12f, 20f })
                lamps.Add(At(x, 12.25f, z + 0.2f));
        }
        AddBatch("int.post.glb", posts);
        AddBatch("int.post.glb", overhead);
        AddBatch("int.fixture.wall-lamp.glb", lamps);
    }

    private void BuildDockedAssembly()
    {
        // The public developer prefab and canonical dresser transforms come from
        // the renderer's immutable asset manifest, with no account or database rows.
        // Showing the interior makes this the same recognisable ship as gameplay.
        var ship = ReplicatedWorld.CreatePreviewAssembly(PresentationLayer, DockedShipCutaway);
        ship.Name = "DockedWayfarer";
        ship.Position = new Vector3(7, 0.3875f - ship.Bounds.Position.Y, -14);
        ship.Rotation = new Vector3(0, Mathf.Pi, 0);
        AddChild(ship);

        AddAsset("console.navigation.sm.glb", At(18, 0.1875f, -1, -Mathf.Pi / 2));
        AddBatch("int.fixture.wall-lamp.glb", new[]
        {
            At(-2, 0.1875f, 1, -Mathf.Pi / 2),
            At(17, 0.1875f, 1, Mathf.Pi / 2),
            At(-2, 0.1875f, -32, -Mathf.Pi / 2),
            At(17, 0.1875f, -32, Mathf.Pi / 2),
        });
    }

    private void BuildCargo()
    {
        var cargo = new List<Transform3D>();
        foreach (var point in new[]
        {
            new Vector3(18.5f, 0.1875f, 8), new Vector3(19.5f, 0.1875f, 8),
            new Vector3(18.5f, 1.1875f, 8), new Vector3(19.5f, 0.1875f, 7),
            new Vector3(-20, 0.1875f, -26), new Vector3(-19, 0.1875f, -26),
            new Vector3(-20, 1.1875f, -26), new Vector3(-21, 0.1875f, -27),
        }) cargo.Add(At(point.X, point.Y, point.Z));
        AddBatch("cargo.standard.medium.glb", cargo);
        AddAsset("cargo.fluid.medium.glb", At(21, 0.1875f, 6));
        AddAsset("cargo.fluid.medium.glb", At(-21.5f, 0.1875f, -25));
        AddAsset("shipyard.equipment.command-console.glb", At(24, 0.1875f, -12, -Mathf.Pi / 2));
        AddAsset("shipyard.equipment.wall-locker.glb", At(-25.1f, 0.1875f, -20, Mathf.Pi / 2));
        AddAsset("shipyard.equipment.wall-locker.glb", At(-25.1f, 0.1875f, -21, Mathf.Pi / 2));
    }

    private void BuildLighting()
    {
        AddChild(new DirectionalLight3D
        {
            Name = "BayKeyLight", RotationDegrees = new Vector3(-48, -28, 0),
            LightColor = new Color("f6dcc3"), LightEnergy = 0.7f,
            Layers = PresentationLayer, LightCullMask = PresentationLayer,
            ShadowEnabled = true, DirectionalShadowMaxDistance = 80,
            ShadowCasterMask = PresentationLayer,
        });
        AddChild(new DirectionalLight3D
        {
            Name = "ApertureFill", RotationDegrees = new Vector3(-32, 150, 0),
            LightColor = new Color("729fc7"), LightEnergy = 0.22f,
            Layers = PresentationLayer, LightCullMask = PresentationLayer,
            ShadowEnabled = false,
        });
        // Six local pools stay below Compatibility's default eight omni lights per
        // mesh. Visible authored emissive fixtures do not require renderer bloom.
        AddLight("ShipRim", new Vector3(12, 5, -30), "51c8ed", 1.8f, 18);
        AddLight("NearServiceLamp", new Vector3(19, 3, 8), "ffa65b", 1.6f, 9);
        AddLight("CargoServiceLamp", new Vector3(-20, 3, -26), "ffb86c", 1.5f, 10);
        AddLight("AisleFill", new Vector3(-11, 3, 5), "4f9abc", 0.9f, 11);
        AddLight("DockWorkLight", new Vector3(7, 7, -6), "ffdda9", 1.25f, 16);
        AddLight("ApertureRim", new Vector3(-10, 5, -38), "698bde", 0.8f, 14);
    }

    private void AddLight(string name, Vector3 position, string color, float energy, float range) => AddChild(new OmniLight3D
    {
        Name = name, Position = position, LightColor = new Color(color), LightEnergy = energy,
        OmniRange = range, Layers = PresentationLayer,
        LightCullMask = PresentationLayer, ShadowEnabled = false,
    });

    private static Transform3D At(float x, float y, float z, float yaw = 0) =>
        new(new Basis(Vector3.Up, yaw), new Vector3(x, y, z));

    private PackedScene LoadAsset(string file)
    {
        if (!scenes.TryGetValue(file, out var scene))
        {
            scene = GD.Load<PackedScene>(Assets + file)
                ?? throw new InvalidOperationException($"Missing frontend asset: {file}");
            scenes.Add(file, scene);
        }
        return scene;
    }

    private void AddAsset(string file, Transform3D transform)
    {
        var node = LoadAsset(file).Instantiate<Node3D>();
        node.Transform = transform;
        ApplyLayers(node);
        AddChild(node);
    }

    private static void ApplyLayers(Node node)
    {
        if (node is VisualInstance3D visual) visual.Layers = PresentationLayer;
        if (node is Light3D light) light.LightCullMask = PresentationLayer;
        foreach (var child in node.GetChildren()) ApplyLayers(child);
    }

    private void AddBatch(string file, IReadOnlyList<Transform3D> placements,
        string? selectedNode = null, Color? localFinish = null)
    {
        var prototype = LoadAsset(file).Instantiate<Node3D>();
        var selected = selectedNode == null ? prototype : prototype.FindChild(selectedNode, true, false)
            ?? throw new InvalidOperationException($"Missing native asset node: {selectedNode}");
        AddMeshes(selected, Transform3D.Identity, placements, file, localFinish);
        prototype.Free();
    }

    private void AddMeshes(Node node, Transform3D parent, IReadOnlyList<Transform3D> placements,
        string file, Color? localFinish)
    {
        var local = parent * (node is Node3D spatial ? spatial.Transform : Transform3D.Identity);
        if (node is MeshInstance3D { Mesh: not null } source)
        {
            var displayMesh = source.Mesh;
            Material? materialOverride = source.MaterialOverride;
            if (localFinish.HasValue && source.Mesh is ArrayMesh authoredMesh)
            {
                var finishedMesh = (ArrayMesh)authoredMesh.Duplicate();
                for (var surface = 0; surface < authoredMesh.GetSurfaceCount(); surface++)
                {
                    if (source.GetActiveMaterial(surface) is not BaseMaterial3D authoredMaterial) continue;
                    var finish = (BaseMaterial3D)authoredMaterial.Duplicate();
                    finish.AlbedoColor *= localFinish.Value;
                    finishedMesh.SurfaceSetMaterial(surface, finish);
                }
                displayMesh = finishedMesh;
                materialOverride = null;
            }
            var batch = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = displayMesh, InstanceCount = placements.Count,
            };
            for (var i = 0; i < placements.Count; i++) batch.SetInstanceTransform(i, placements[i] * local);
            AddChild(new MultiMeshInstance3D
            {
                Name = file.Replace('.', '_') + "_batch", Multimesh = batch,
                Layers = PresentationLayer, MaterialOverride = materialOverride,
            });
        }
        foreach (var child in node.GetChildren()) AddMeshes(child, local, placements, file, localFinish);
    }
}
