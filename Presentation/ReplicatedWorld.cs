using Godot;
using System;
using System.Linq;
using Sidereal.Native;

/// <summary>Presentation of the actor-filtered current ship; owns no simulation state.</summary>
public partial class ReplicatedWorld : Node3D
{
    private const uint WorldLayer = 1u;
    private static ReplicatedWorldCatalog? catalog;
    public static ReplicatedWorldCatalog Catalog => catalog ??= new ReplicatedWorldCatalog(FileAccess.GetFileAsString("res://Assets/World/manifest.json"));
    private Node3D shipRoot = null!;
    private ReplicatedWorldAssembly? assembly;
    private Node3D characterMarker = null!;
    private string? layoutKey;
    private bool preview;
    private bool framed;
    private Rect2 requestedPresentationBounds;
    public Rect2 PresentationBounds { get; private set; }
    public Rect2 ProjectedShipBounds { get; private set; }
    public Camera3D Camera { get; private set; } = null!;
    public string Status { get; private set; } = "Awaiting your ship.";
    public int LoadedAssetCount => assembly?.LoadedAssetCount ?? 0;
    public int RenderedPlacementCount => assembly?.RenderedPlacementCount ?? 0;
    public string[] MissingAssetIds => assembly?.MissingAssetIds.ToArray() ?? Array.Empty<string>();
    public string? PrefabId => assembly?.PrefabId;
    public bool IsPreview => preview;

    public override void _Ready()
    {
        Name = "ReplicatedWorld";
        shipRoot = new Node3D { Name = "ServerShipFrame" }; AddChild(shipRoot);
        Camera = new Camera3D
        {
            Name = "GameplayCamera", Position = new Vector3(24, 30, 32), Projection = Camera3D.ProjectionType.Orthogonal,
            Size = 28, KeepAspect = Camera3D.KeepAspectEnum.Height, CullMask = WorldLayer, Near = .1f, Far = 1200,
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color("02050a"),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color("a3b5d5"),
                AmbientLightEnergy = .30f, ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        };
        AddChild(Camera); Camera.LookAt(Vector3.Zero, Vector3.Up);
        AddChild(new DirectionalLight3D
        {
            Name = "SpaceKey", RotationDegrees = new Vector3(-55, -35, 0), LightColor = new Color("f5f4ec"),
            LightEnergy = .78f, ShadowEnabled = true, DirectionalShadowMaxDistance = 65,
            Layers = WorldLayer, LightCullMask = WorldLayer,
        });
        AddChild(new DirectionalLight3D
        {
            Name = "SpaceFill", RotationDegrees = new Vector3(-35, 130, 0), LightColor = new Color("789cca"),
            LightEnergy = .18f, ShadowEnabled = false, Layers = WorldLayer, LightCullMask = WorldLayer,
        });
        BuildStars();
        // Explicit positional marker pending the owner's existing character/armor lane.
        // It deliberately does not substitute an undesired current character model.
        characterMarker = new Node3D { Name = "CharacterPositionMarker", Visible = false };
        characterMarker.AddChild(new MeshInstance3D
        {
            Name = "PositionRing", Mesh = new TorusMesh { InnerRadius = .24f, OuterRadius = .29f, Rings = 20, RingSegments = 6 }, Layers = WorldLayer,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("39daf3"), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
        });
        shipRoot.AddChild(characterMarker);
    }

    public void SetPreview(bool enabled)
    {
        if (preview == enabled) return;
        preview = enabled; ClearAssembly();
    }

    /// <summary>Physical viewport pixels left free by the responsive gameplay HUD.</summary>
    public void SetPresentationBounds(Rect2 bounds) => requestedPresentationBounds = bounds;

    private void ClearAssembly()
    {
        if (assembly != null) { shipRoot.RemoveChild(assembly); assembly.QueueFree(); assembly = null; }
        layoutKey = null; framed = false; ProjectedShipBounds = default;
        if (characterMarker != null) characterMarker.Visible = false;
    }

    public static ReplicatedWorldAssembly CreatePreviewAssembly(uint layers = WorldLayer, bool cutaway = true, string previewPrefabId = "fed.m.wayfarer") =>
        new(Catalog.Preview(previewPrefabId), "{}", layers, cutaway);

    public static ReplicatedWorldAssembly BuildAssembly(string documentJson, string furnishingsJson, string? deckId, uint layers = WorldLayer, bool cutaway = true) =>
        new(Catalog.Match(documentJson, deckId), furnishingsJson, layers, cutaway);

    public void Sync(ClientCore core, double delta, bool visible)
    {
        Visible = visible; Camera.Current = visible;
        if (!preview && (core.Instance == null || core.Connection == null))
        {
            ClearAssembly();
            Status = "Awaiting your replicated ship.";
        }
        if (!visible) return;
        var instance = core.Instance;
        var key = preview ? "published-wayfarer-preview" : instance == null ? null : $"{instance.Id}:{instance.Revision}:{instance.FurnishingRevision}:{core.Location?.DeckId}";
        if (key != layoutKey)
        {
            layoutKey = key; framed = false;
            if (assembly != null) { shipRoot.RemoveChild(assembly); assembly.QueueFree(); assembly = null; }
            try
            {
                assembly = preview ? CreatePreviewAssembly() : instance == null ? null :
                    BuildAssembly(instance.DocumentJson, instance.FurnishingsJson, core.Location?.DeckId);
                if (assembly != null) shipRoot.AddChild(assembly);
                Status = assembly == null ? "Awaiting your replicated ship." :
                    preview ? "Published Wayfarer preview · local presentation only" :
                    assembly.VisualKind.StartsWith("current-authored", StringComparison.Ordinal) ? "Authored ship · server-confirmed layout and furnishings" :
                    "Server-confirmed prefab · legacy kit presentation";
                if (MissingAssetIds.Length != 0) Status += " · missing mesh assets";
            }
            catch (Exception error)
            {
                Status = error is InvalidOperationException ? error.Message : "This ship needs an updated native asset renderer.";
                GD.PushWarning($"Native ship assembly is unavailable ({error.GetType().Name}).");
            }
        }
        if (assembly == null) { characterMarker.Visible = false; return; }
        // Follow the owned ship's double-precision world origin. The ship stays near
        // render zero, while local actor coordinates and authoritative heading remain exact.
        var vessel = core.Ship;
        var originX = vessel?.X ?? 0; var originY = vessel?.Y ?? 0;
        var relative = RelativePose.World(vessel?.X ?? 0, vessel?.Y ?? 0, originX, originY);
        shipRoot.Position = new Vector3((float)relative.X, 0, (float)relative.Z);
        // Public preview uses the owner's three-quarter side composition, bow lower-left.
        // Connected ships keep their actual server heading.
        shipRoot.Rotation = new Vector3(0, preview ? 2.9f : (float)(vessel?.Heading ?? 0), 0);
        assembly.SetCutaway(true);
        var actor = core.Character;
        characterMarker.Visible = !preview && actor != null && !core.IsPiloting;
        if (actor != null)
        {
            var local = RelativePose.World(actor.LocalX, actor.LocalY, 0, 0, (core.Location?.StandingElevationM ?? .1875) + .035);
            characterMarker.Position = new Vector3((float)local.X, (float)local.Height, (float)local.Z);
        }
        var target = shipRoot.Transform * assembly.Bounds.GetCenter();
        var desired = target + new Vector3(24, 29, 32);
        var factor = framed ? 1 - Math.Exp(-delta * 5) : 1;
        Camera.Position = Camera.Position.Lerp(desired, (float)factor); Camera.LookAt(target, Vector3.Up);
        assembly.UpdateCutaway(shipRoot.GlobalTransform.AffineInverse() * Camera.GlobalPosition);
        FitPresentationBounds(target);
        framed = true;
    }

    private void FitPresentationBounds(Vector3 target)
    {
        var viewport = GetViewport().GetVisibleRect();
        var width = Math.Max(1, viewport.Size.X); var height = Math.Max(1, viewport.Size.Y);
        var available = requestedPresentationBounds.Size.X > 0 && requestedPresentationBounds.Size.Y > 0 ?
            requestedPresentationBounds.Intersection(viewport) : new Rect2(viewport.Position + new Vector2(16, height * .035f),
                new Vector2(Math.Max(1, width - 32), height * .78f));
        if (available.Size.X < 1 || available.Size.Y < 1) available = viewport;
        PresentationBounds = available;
        var geometry = assembly!.GetFramingBounds(shipRoot.GlobalTransform, Camera.GlobalBasis, target);
        if (geometry.Size.X <= 0 || geometry.Size.Y <= 0) { ProjectedShipBounds = default; return; }
        var span = geometry.Size;
        var required = Math.Max(span.X * height / available.Size.X, span.Y * height / available.Size.Y) * 1.06f;
        Camera.Size = Math.Max(16, required);
        var center = available.GetCenter() - viewport.Position;
        // Godot camera offsets move the image opposite the camera's local X/up.
        // Vertical pixel coordinates grow downward; horizontal ones grow right.
        var geometryCenter = geometry.GetCenter();
        Camera.VOffset = geometryCenter.Y + (center.Y / height - .5f) * Camera.Size;
        Camera.HOffset = geometryCenter.X + (.5f - center.X / width) * Camera.Size * width / height;
        var pixelsPerMeter = height / Camera.Size;
        ProjectedShipBounds = new Rect2(viewport.Position + new Vector2(width * .5f + (geometry.Position.X - Camera.HOffset) * pixelsPerMeter,
            height * .5f - (geometry.End.Y - Camera.VOffset) * pixelsPerMeter), geometry.Size * pixelsPerMeter);
    }

    private void BuildStars()
    {
        var random = new Random(191211);
        var stars = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new SphereMesh { Radius = .06f, Height = .12f, RadialSegments = 6, Rings = 2 }, InstanceCount = 1600,
        };
        for (var index = 0; index < stars.InstanceCount; index++)
        {
            var point = new Vector3((float)(random.NextDouble() - .5) * 500, -55 - (float)random.NextDouble() * 180, (float)(random.NextDouble() - .5) * 500);
            stars.SetInstanceTransform(index, new Transform3D(Basis.Identity, point));
        }
        AddChild(new MultiMeshInstance3D
        {
            Name = "DistantStarfieldPresentation", Multimesh = stars, Layers = WorldLayer,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("71849c"), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }
}
