using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Local presentation only. These are the browser game's published, metre-scale GLBs,
/// assembled into an interim docking concourse; they do not represent a server world.
/// </summary>
public partial class FrontendBackdrop : Node3D
{
    [Export] public Vector3 PresentationCameraPosition { get; set; } = new(11, 6.4f, 13);
    [Export] public Vector3 PresentationCameraTarget { get; set; } = new(0, 1.1f, -9);
    [Export(PropertyHint.Range, "35,80,1")] public float PresentationFieldOfView { get; set; } = 56;
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
        // The game's existing sky plate is distant scenery only. Every nearby
        // wall, deck, fitting and cargo object above is actually rendered geometry.
        AddChild(new MeshInstance3D
        {
            Name = "OrionVista", Position = new Vector3(-45, -12, -37),
            Rotation = new Vector3(0, 0.464f, 0), Layers = PresentationLayer,
            Mesh = new QuadMesh { Size = new Vector2(140, 92) },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoTexture = GD.Load<Texture2D>(Assets + "orion-veil-v1.png"),
                AlbedoColor = new Color(0.82f, 0.82f, 0.82f),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        });
        camera = new Camera3D
        {
            Name = "PresentationCamera",
            Position = PresentationCameraPosition,
            Fov = PresentationFieldOfView,
            Near = 0.1f,
            Far = 160,
            CullMask = PresentationLayer,
            Environment = CreateEnvironment(),
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

    private Godot.Environment CreateEnvironment()
    {
        var panorama = new PanoramaSkyMaterial
        {
            Panorama = GD.Load<Texture2D>(Assets + "orion-veil-v1.png"),
            Filter = true,
            EnergyMultiplier = 0.52f,
        };
        return new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = panorama },
            SkyRotation = new Vector3(0, 0.8f, 0),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("8290bc"),
            AmbientLightEnergy = 0.3f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };
    }

    private void BuildConcourse()
    {
        // The installed native deck panel is two metres across. One-metre corridor
        // pieces use the same datum. All meshes keep their actual exported scale.
        var floor = new List<Transform3D>();
        var aisle = new List<Transform3D>();
        for (var x = -12; x < 12; x += 2)
        for (var z = -18; z <= 10; z += 2)
        {
            if (x == -4)
            {
                aisle.Add(At(x, 0, z)); aisle.Add(At(x + 1, 0, z));
                aisle.Add(At(x, 0, z - 1)); aisle.Add(At(x + 1, 0, z - 1));
            }
            else floor.Add(At(x, 0, z));
        }
        AddBatch("native-deck-kit.glb", floor, "GEO-part-a3f5c1c3caa171a94d6c--floor");
        AddBatch("int.floor.corridor.glb", aisle);

        var panels = new List<Transform3D>();
        var braces = new List<Transform3D>();
        var lamps = new List<Transform3D>();
        // Open space beyond the far edge. The two service walls are reused native
        // interior modules, not a qualified pressure hull or a finished hangar kit.
        for (var z = -19; z < 10; z++)
        {
            panels.Add(At(-12, 0, z, Mathf.Pi / 2));
            braces.Add(At(12, 0, z - 1, -Mathf.Pi / 2));
            if (z % 3 == 0)
            {
                lamps.Add(At(-11.8f, 1.8f, z - 0.31f, Mathf.Pi / 2));
                lamps.Add(At(11.8f, 1.8f, z - 0.69f, -Mathf.Pi / 2));
            }
        }
        AddBatch("int.edge.panel.glb", panels);
        AddBatch("int.edge.reinforced.glb", braces);
        AddBatch("int.fixture.wall-lamp.glb", lamps);

        // Three reusable trusses frame the open vista, leaving a quiet left region
        // for login and a bright docking bay on the right.
        var posts = new List<Transform3D>();
        foreach (var z in new[] { -16f, -8f, 0f })
        {
            for (var height = 0; height < 3; height++)
            {
                posts.Add(At(-12, height * 2.125f, z));
                posts.Add(At(11.625f, height * 2.125f, z));
            }
        }
        AddBatch("int.post.glb", posts);
        // The existing one-metre posts also form the overhead braces, placed rather
        // than rescaled. Their local geometry is presentation, not a build contract.
        var overhead = new List<Transform3D>();
        foreach (var z in new[] { -16f, -8f, 0f })
        for (var x = -12; x < 12; x += 2)
            overhead.Add(new Transform3D(new Basis(Vector3.Forward, Mathf.Pi / 2), new Vector3(x + 2, 6.3f, z)));
        AddBatch("int.post.glb", overhead);
    }

    private void BuildDockedAssembly()
    {
        // A presentation assembly of current ship modules at 1:1 scale. No blueprint
        // is published, and no live ship or content identity is changed by this scene.
        var body = new List<Transform3D>();
        for (var x = 3; x < 7; x++)
        for (var z = -11; z < -7; z++) body.Add(At(x, 0.1875f, z));
        AddBatch("hull.square.wing.glb", body);
        var cabin = new List<Transform3D>();
        for (var x = 4; x < 6; x++)
        for (var z = -13; z < -10; z++) cabin.Add(At(x, 0.1875f, z));
        AddBatch("hull.square.deck.glb", cabin);
        AddBatch("hull.slope1.wing.glb", new[] { At(3, 0.1875f, -12), At(6, 0.1875f, -12, Mathf.Pi / 2) });
        AddBatch("hull.slope1.deck.glb", new[] { At(4, 0.1875f, -14), At(5, 0.1875f, -14, Mathf.Pi / 2) });
        AddAsset("ion-drive.sm.glb", At(3.5f, 1.4f, -7));
        AddAsset("ion-drive.sm.glb", At(6.5f, 1.4f, -7));
        AddAsset("console.navigation.sm.glb", At(8.5f, 0.1875f, -5, -Mathf.Pi / 2));
        // Floor lamps highlight the dock without requiring bloom to read the asset.
        AddBatch("int.fixture.wall-lamp.glb", new[]
        {
            At(2.4f, 0.1875f, -6, -Mathf.Pi / 2),
            At(7.8f, 0.1875f, -6, Mathf.Pi / 2),
            At(2.4f, 0.1875f, -13, -Mathf.Pi / 2),
            At(7.8f, 0.1875f, -13, Mathf.Pi / 2),
        });
    }

    private void BuildCargo()
    {
        var cargo = new List<Transform3D>();
        foreach (var point in new[]
        {
            new Vector3(10, 0.1875f, 2), new Vector3(11, 0.1875f, 2),
            new Vector3(10, 1.1875f, 2), new Vector3(11, 0.1875f, 1),
            new Vector3(-9, 0.1875f, -13), new Vector3(-8, 0.1875f, -13),
            new Vector3(-9, 1.1875f, -13), new Vector3(-10, 0.1875f, -14),
        }) cargo.Add(At(point.X, point.Y, point.Z));
        AddBatch("cargo.standard.medium.glb", cargo);
        AddAsset("cargo.fluid.medium.glb", At(9.5f, 0.1875f, 0));
        AddAsset("cargo.fluid.medium.glb", At(-10.5f, 0.1875f, -12));
        AddAsset("shipyard.equipment.command-console.glb", At(10.7f, 0.1875f, -8, -Mathf.Pi / 2));
        AddAsset("shipyard.equipment.wall-locker.glb", At(-11.1f, 0.1875f, -9, Mathf.Pi / 2));
        AddAsset("shipyard.equipment.wall-locker.glb", At(-11.1f, 0.1875f, -10, Mathf.Pi / 2));
    }

    private void BuildLighting()
    {
        AddChild(new DirectionalLight3D
        {
            Name = "BayKeyLight", RotationDegrees = new Vector3(-55, -35, 0),
            LightColor = new Color("bed9ef"), LightEnergy = 0.42f,
            Layers = PresentationLayer, LightCullMask = PresentationLayer,
            ShadowEnabled = true, DirectionalShadowMaxDistance = 50,
        });
        AddLight(new Vector3(7, 4, -10), "36caff", 1.05f, 14);
        AddLight(new Vector3(10, 2.5f, 1), "ffaa6e", 0.75f, 9);
        AddLight(new Vector3(-10, 3, -12), "ffba89", 0.65f, 9);
        AddLight(new Vector3(1, 5, -18), "6a70ef", 0.9f, 15);
    }

    private void AddLight(Vector3 position, string color, float energy, float range) => AddChild(new OmniLight3D
    {
        Position = position, LightColor = new Color(color), LightEnergy = energy,
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
        foreach (var child in node.GetChildren()) ApplyLayers(child);
    }

    private void AddBatch(string file, IReadOnlyList<Transform3D> placements, string? selectedNode = null)
    {
        // Retain authored meshes/materials and batch repeated pieces. Hundreds of
        // floor cells need a handful of draw surfaces rather than hundreds of nodes.
        var prototype = LoadAsset(file).Instantiate<Node3D>();
        var selected = selectedNode == null ? prototype : prototype.FindChild(selectedNode, true, false)
            ?? throw new InvalidOperationException($"Missing native asset node: {selectedNode}");
        AddMeshes(selected, Transform3D.Identity, placements, file);
        prototype.Free();
    }

    private void AddMeshes(Node node, Transform3D parent, IReadOnlyList<Transform3D> placements, string file)
    {
        var local = parent * (node is Node3D spatial ? spatial.Transform : Transform3D.Identity);
        if (node is MeshInstance3D { Mesh: not null } source)
        {
            var batch = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = source.Mesh,
                InstanceCount = placements.Count,
            };
            for (var i = 0; i < placements.Count; i++) batch.SetInstanceTransform(i, placements[i] * local);
            AddChild(new MultiMeshInstance3D
            {
                Name = file.Replace('.', '_') + "_batch", Multimesh = batch,
                Layers = PresentationLayer, MaterialOverride = source.MaterialOverride,
            });
        }
        foreach (var child in node.GetChildren()) AddMeshes(child, local, placements, file);
    }
}
