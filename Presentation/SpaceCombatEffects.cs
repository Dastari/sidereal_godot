using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sidereal.Native.Input;
using FileAccess = Godot.FileAccess;

namespace Sidereal.Native;

/// <summary>Muzzle coordinates are already in the renderer's origin-subtracted global frame.</summary>
public sealed record CombatBodyAnchor(double StandingHeight, CombatPoint3? MuzzleRenderPosition = null, CombatPoint3? MuzzleRenderDirection = null,
    CombatPoint3? BodyRenderPosition = null);
public sealed record CombatPresentationFrame(Node3D ShipRoot, string ShipId, double OriginX, double OriginY,
    double StandingElevation, bool InteriorVisible, bool Active, uint RenderLayers = 1, bool ReducedMotion = false,
    bool LegacyOwnImpact = false, Node3D? WorldRoot = null);

/// <summary>Immutable accepted combat events only. No raycast, damage, inventory or reducer calls.</summary>
public partial class SpaceCombatEffects : Node3D
{
    private const string AssetBase = "res://Assets/Combat/";
    private const int MaxLive = 96;
    private sealed record Spawn(string ActorId, string FxId, CombatPoint3 At, CombatPoint3? Direction = null,
        CombatPoint3? To = null, double? Length = null, double Size = 1, CombatPoint3? Tint = null,
        double? Hold = null, bool World = false, Spawn? After = null);
    private sealed class Live
    {
        public Spawn Spawn = null!; public Node3D Root = null!; public CombatFxTimeline Clock = null!;
        public readonly List<(BaseMaterial3D Material, Color Albedo, float Emission)> Materials = new();
    }
    private sealed class Legacy
    {
        public string ActorId = ""; public double Start, Last; public bool World;
        public CombatPoint3 At, End; public Node3D Root = null!; public bool Impact;
        public readonly List<MeshInstance3D> Meshes = new();
        public readonly List<CombatPoint3> SparkOffsets = new();
        public BaseMaterial3D Material = null!;
    }
    private sealed class Thrown
    {
        public string ActorId = "", ItemId = "", File = "";
        public CombatPoint3 From, To; public double Start, Rotation;
        public Node3D? Root;
    }
    private static CombatEffectCatalog? catalog;
    private static readonly Dictionary<string, PackedScene> scenes = new(StringComparer.Ordinal);
    private static readonly HashSet<string> requested = new(StringComparer.Ordinal), failed = new(StringComparer.Ordinal);
    private readonly CombatActionObserver actions = new();
    private readonly Dictionary<string, ulong> evaSequences = new(StringComparer.Ordinal);
    private readonly HashSet<string> allowed = new(StringComparer.Ordinal), interiorActors = new(StringComparer.Ordinal), worldActors = new(StringComparer.Ordinal), missing = new(StringComparer.Ordinal);
    private readonly List<Spawn> pending = new();
    private readonly List<Live> live = new();
    private readonly List<Legacy> legacy = new();
    private readonly Dictionary<string, Thrown> thrown = new(StringComparer.Ordinal);
    private string context = ""; private ulong? ownImpactSequence; private double lastTime;
    private CombatPresentationFrame frame = null!;
    public int LiveCount => live.Count + legacy.Count + thrown.Count;
    public int PendingCount => pending.Count + thrown.Values.Count(value => value.Root == null);
    public int AcceptedShots { get; private set; }
    public int AcceptedImpacts { get; private set; }
    public string[] MissingAssets => missing.ToArray();
    public static CombatEffectCatalog Catalog => catalog ??= new(FileAccess.GetFileAsString(AssetBase + "manifest.json"));

    private static PackedScene? Scene(string file, out bool unavailable)
    {
        unavailable = false; var path = AssetBase + file;
        if (scenes.TryGetValue(path, out var cached)) return cached;
        if (failed.Contains(path)) { unavailable = true; return null; }
        if (requested.Add(path) && ResourceLoader.LoadThreadedRequest(path, "PackedScene") != Error.Ok)
        { failed.Add(path); unavailable = true; return null; }
        var status = ResourceLoader.LoadThreadedGetStatus(path);
        if (status == ResourceLoader.ThreadLoadStatus.Loaded)
        {
            var loaded = ResourceLoader.LoadThreadedGet(path) as PackedScene;
            if (loaded != null) { scenes[path] = loaded; return loaded; }
            failed.Add(path); unavailable = true;
        }
        else if (status is ResourceLoader.ThreadLoadStatus.Failed or ResourceLoader.ThreadLoadStatus.InvalidResource)
        { failed.Add(path); unavailable = true; }
        return null;
    }
    private static Vector3 Vector(CombatPoint3 point) => new((float)point.X, (float)point.Y, (float)point.Z);
    private Vector3 EffectPosition(CombatPoint3 point, bool world) => world
        ? Vector(new(point.X - frame.OriginX, point.Y, point.Z + frame.OriginY)) : Vector(point);
    private Node3D ParentFor(bool world) => world ? frame.WorldRoot ?? this : frame.ShipRoot;
    private CombatPoint3 RenderToShip(CombatPoint3 position)
    { var local = frame.ShipRoot.GlobalTransform.AffineInverse() * Vector(position); return new(local.X, local.Y, local.Z); }
    private CombatPoint3 DirectionToShip(CombatPoint3 direction)
    { var local = frame.ShipRoot.GlobalBasis.Inverse() * Vector(direction); return new CombatPoint3(local.X, local.Y, local.Z).Normalized; }
    private static void Orient(Node3D node, CombatPoint3 direction)
    {
        if (!direction.Finite || direction.Length < 1e-9) return;
        var rotation = CombatEffectProjection.ForwardRotation(direction);
        node.Basis = new Basis(new Quaternion(Vector(rotation.Axis), (float)rotation.Angle));
    }
    private static IEnumerable<MeshInstance3D> Meshes(Node node)
    {
        if (node is MeshInstance3D mesh) yield return mesh;
        foreach (var child in node.GetChildren()) foreach (var descendant in Meshes(child)) yield return descendant;
    }
    private static void Remove(Node3D? node)
    { if (node != null && GodotObject.IsInstanceValid(node)) { node.Visible = false; node.QueueFree(); } }
    public void Clear()
    {
        foreach (var effect in live) Remove(effect.Root);
        foreach (var effect in legacy) Remove(effect.Root);
        foreach (var effect in thrown.Values) Remove(effect.Root);
        live.Clear(); legacy.Clear(); pending.Clear(); thrown.Clear(); allowed.Clear(); interiorActors.Clear(); worldActors.Clear(); missing.Clear();
        actions.Clear(); evaSequences.Clear(); ownImpactSequence = null; AcceptedShots = AcceptedImpacts = 0;
    }
    public override void _ExitTree() => Clear();

    public void Sync(ClientCore core, CombatPresentationFrame acceptedFrame, double monotonicSeconds,
        Func<string, CombatBodyAnchor?>? bodyOf = null)
    {
        frame = acceptedFrame;
        var actor = core.Character; var connection = core.Connection;
        if (!frame.Active || connection == null || actor?.Connected != true || !core.SharedAdmissionReady ||
            !double.IsFinite(monotonicSeconds) || !double.IsFinite(frame.OriginX) || !double.IsFinite(frame.OriginY) ||
            !double.IsFinite(frame.StandingElevation) || !GodotObject.IsInstanceValid(frame.ShipRoot) ||
            frame.ShipId != (core.CurrentPresentedShip?.Id ?? ""))
        { Clear(); context = ""; lastTime = monotonicSeconds; return; }
        var deck = core.Location?.DeckId ?? core.PassengerInterior?.DeckId;
        var sceneKey = $"{connection.ConnectionId}/{core.SharedWorldEpoch}/{actor.Id}/{frame.ShipId}/{deck}/{core.Location?.VisitId}/{core.Instance?.Id}/{core.Instance?.Revision}/{core.Instance?.FurnishingRevision}/{core.Eva?.Phase}/{frame.InteriorVisible}/{frame.LegacyOwnImpact}/{frame.ShipRoot.GetInstanceId()}";
        if (sceneKey != context) { Clear(); context = sceneKey; lastTime = monotonicSeconds; }
        var delta = Math.Max(0, monotonicSeconds - lastTime); lastTime = monotonicSeconds;
        allowed.Clear(); interiorActors.Clear(); worldActors.Clear();
        var interiorReady = core.Eva == null && frame.InteriorVisible && deck != null &&
            (core.Location?.InstanceId == core.Instance?.Id && core.Instance != null || core.PassengerInterior?.ShipId == frame.ShipId);
        var rows = interiorReady ? connection.Db.VisibleCombatActions.Iter().Where(row => row.ShipId == frame.ShipId && row.DeckId == deck)
            .Select(row => new CombatActionInput(row.CharacterId, row.ShipId, row.DeckId, CrewAssets.Catalog.DefinitionHeld(row.DefinitionId), row.Mode,
                row.ShotSequence, row.OriginX, row.OriginY, row.PointsJson, row.LandX, row.LandY, row.Detonated, row.BlastRadiusM, row.ReloadSequence, row.StunSequence)).ToArray()
            : Array.Empty<CombatActionInput>();
        foreach (var row in rows) { allowed.Add(row.CharacterId); interiorActors.Add(row.CharacterId); }
        var changes = actions.Observe(rows);
        foreach (var change in changes.Events) Play(change, monotonicSeconds, bodyOf?.Invoke(change.Action.CharacterId));
        if (core.SpatialReady)
        {
            var admission = connection.Db.OwnWorldAdmission.Iter().FirstOrDefault(row => row.CharacterId == actor.Id);
            var evaRows = admission == null ? Array.Empty<Sidereal.Bindings.VisibleEvaBody>() : connection.Db.VisibleEvaBodies.Iter()
                .Where(row => row.CharacterId != actor.Id && row.SystemId == admission.SystemId).ToArray();
            var keepEva = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in evaRows)
            {
                allowed.Add(row.CharacterId); worldActors.Add(row.CharacterId); keepEva.Add(row.CharacterId);
                // Source remote-crew primes its shot sequence only once a full
                // authored body is ready; loading a body must not replay shots.
                var body = bodyOf?.Invoke(row.CharacterId);
                if (body == null) { evaSequences.Remove(row.CharacterId); continue; }
                if (evaSequences.TryGetValue(row.CharacterId, out var previous) && previous != row.ShotSequence && row.ShotSequence > 0)
                {
                    var height = body.StandingHeight;
                    var physical = body.MuzzleRenderPosition ??
                        (body.BodyRenderPosition is { } bodyPosition ? bodyPosition + new CombatPoint3(0, CombatEffectProjection.BeamHeight, 0) : (CombatPoint3?)null);
                    var origin = physical is { } rendered
                        ? new CombatPoint3(rendered.X + frame.OriginX, rendered.Y, rendered.Z - frame.OriginY)
                        : new CombatPoint3(row.X, height + CombatEffectProjection.BeamHeight, -row.Y);
                    var end = new CombatPoint3(row.ShotX, height + CombatEffectProjection.BeamHeight, -row.ShotY);
                    if (origin.Finite && end.Finite)
                    {
                        AcceptedShots++;
                        if (!frame.ReducedMotion && (end - origin).Length >= .05) Tracer(row.CharacterId, origin, end, monotonicSeconds);
                        if (row.ShotStruck) Impact(row.CharacterId, new(row.ShotX, CombatEffectProjection.LegacyImpactHeight, -row.ShotY), true, monotonicSeconds);
                    }
                }
                evaSequences[row.CharacterId] = row.ShotSequence;
            }
            foreach (var id in evaSequences.Keys.Where(id => !keepEva.Contains(id)).ToArray()) evaSequences.Remove(id);
        }
        else evaSequences.Clear();
        // The browser suppresses this legacy own flash whenever its r001 FX player
        // exists. Own EVA has no visible deck action row: no invented r001 EVA event.
        if (frame.LegacyOwnImpact)
        {
            var impact = connection.Db.OwnCombatImpact.Iter().FirstOrDefault(row => row.CharacterId == actor.Id && row.ShipId == (core.Eva != null ? "" : frame.ShipId));
            if (impact != null)
            {
                allowed.Add(actor.Id);
                (core.Eva != null ? worldActors : interiorActors).Add(actor.Id);
                if (ownImpactSequence.HasValue && ownImpactSequence.Value != impact.ShotSequence && impact.Kind != "none" && double.IsFinite(impact.X) && double.IsFinite(impact.Y))
                    Impact(actor.Id, new(impact.X, CombatEffectProjection.LegacyImpactHeight, -impact.Y), core.Eva != null, monotonicSeconds);
                ownImpactSequence = impact.ShotSequence;
            }
            else ownImpactSequence = null;
        }
        else ownImpactSequence = null;
        DisposeDenied();
        PumpPending(); UpdateLive(delta); UpdateLegacy(monotonicSeconds); UpdateThrown(monotonicSeconds);
    }

    private void Play(CombatVisualEvent change, double now, CombatBodyAnchor? body)
    {
        var action = change.Action; var height = body?.StandingHeight ?? 0;
        var bodyPoint = body?.BodyRenderPosition is { } bodyPosition ? RenderToShip(bodyPosition) : (CombatPoint3?)null;
        if (change.Kind == CombatVisualEventKind.Stun)
        { if (bodyPoint.HasValue) Enqueue(new(action.CharacterId, "stun-arc", bodyPoint.Value + new CombatPoint3(0, .6, 0), new(0, 1, 0), Length: .9)); return; }
        if (change.Kind == CombatVisualEventKind.Reload || !Catalog.HasItem(action.ItemId)) return;
        if (change.Kind == CombatVisualEventKind.Detonation)
        {
            if (thrown.Remove(action.CharacterId, out var old)) Remove(old.Root);
            var at = new CombatPoint3(action.LandX, height + .2, -action.LandY);
            if (!at.Finite || !double.IsFinite(action.BlastRadius)) return;
            var size = Math.Max(1, action.BlastRadius / .45);
            Enqueue(new(action.CharacterId, Catalog.ItemFx(action.ItemId, "impact") ?? "impact-spark", at, Size: size * .9));
            Enqueue(new(action.CharacterId, "impact-spark", at + new CombatPoint3(0, .3, 0), Size: size * .6));
            Enqueue(new(action.CharacterId, Catalog.ItemFx(action.ItemId, "after") ?? "smoke-puff", at, Size: size * .8));
            return;
        }
        var muzzle = body?.MuzzleRenderPosition is { } physical ? RenderToShip(physical) : (CombatPoint3?)null;
        var direction = body?.MuzzleRenderDirection is { } physicalDirection ? DirectionToShip(physicalDirection) : (CombatPoint3?)null;
        var shot = CombatEffectProjection.Shot(action, height, muzzle, direction);
        if (!shot.Origin.Finite || !shot.Direction.Finite) return;
        AcceptedShots++;
        if (action.Mode == "thrown")
        {
            if (thrown.Remove(action.CharacterId, out var old)) Remove(old.Root);
            var file = Catalog.ThrownFile(action.ItemId); var to = new CombatPoint3(action.LandX, height + .1, -action.LandY);
            if (file == null || !to.Finite) return;
            thrown[action.CharacterId] = new() { ActorId = action.CharacterId, ItemId = action.ItemId!, File = file, From = shot.Origin, To = to, Start = now };
            return;
        }
        if (action.Mode == "melee")
        {
            if (shot.Rays.FirstOrDefault() is { Struck: true } ray)
            {
                var id = Catalog.ItemFx(action.ItemId, "hit");
                if (id != null) Enqueue(new(action.CharacterId, id, shot.Origin, ray.End - shot.Origin, Length: Math.Max(.2, (ray.End - shot.Origin).Length)));
                var impact = Catalog.ItemFx(action.ItemId, "impact");
                if (impact != null) { Enqueue(new(action.CharacterId, impact, ray.End)); AcceptedImpacts++; }
            }
            return;
        }
        void ItemFx(string trigger, CombatPoint3 at, CombatPoint3? forward = null, double size = 1)
        { if (Catalog.ItemFx(action.ItemId, trigger) is { } id) Enqueue(new(action.CharacterId, id, at, forward, Size: size, Tint: Catalog.Tint(action.ItemId, id))); }
        ItemFx("fire", shot.Origin, shot.Direction);
        var projectile = Catalog.ItemFx(action.ItemId, "projectile");
        foreach (var ray in shot.Rays)
        {
            var impactId = ray.Struck ? Catalog.ItemFx(action.ItemId, "impact") : null;
            var impact = impactId == null ? null : new Spawn(action.CharacterId, impactId, ray.End, shot.Direction * -1, Tint: Catalog.Tint(action.ItemId, impactId));
            var length = (ray.End - shot.Origin).Length;
            if (impact != null) AcceptedImpacts++;
            if (projectile == null || length < .05 || Catalog.Effect(projectile) is not { } effect)
            { if (impact != null) Enqueue(impact); continue; }
            if (effect.Kind == "projectile") Enqueue(new(action.CharacterId, projectile, shot.Origin, To: ray.End, Tint: Catalog.Tint(action.ItemId, projectile), After: impact));
            else
            {
                Enqueue(new(action.CharacterId, projectile, shot.Origin, ray.End - shot.Origin, Length: length,
                    Tint: Catalog.Tint(action.ItemId, projectile), Hold: effect.Loop ? effect.Duration * .8 : null));
                if (impact != null) Enqueue(impact);
            }
        }
        // Source after-effects (smoke) preserve their authored fixed colours.
        if (Catalog.ItemFx(action.ItemId, "after") is { } after) Enqueue(new(action.CharacterId, after, shot.Origin, shot.Direction, Size: .6));
    }

    private void Enqueue(Spawn spawn)
    {
        if (!spawn.At.Finite || spawn.To is { Finite: false } || !Allowed(spawn.ActorId, spawn.World)) return;
        if (Catalog.Effect(spawn.FxId) == null) { missing.Add(spawn.FxId); return; }
        pending.Add(spawn);
        while (pending.Count > MaxLive) pending.RemoveAt(0);
    }
    private void PumpPending()
    {
        foreach (var spawn in pending.ToArray())
        {
            var definition = Catalog.Effect(spawn.FxId)!;
            var scene = Scene(definition.File, out var unavailable);
            if (scene == null) { if (unavailable) { missing.Add(definition.File); pending.Remove(spawn); } continue; }
            pending.Remove(spawn);
            if (!Allowed(spawn.ActorId, spawn.World)) continue;
            var node = new Node3D { Name = "AcceptedCombat_" + definition.Id };
            ParentFor(spawn.World).AddChild(node); node.AddChild(scene.Instantiate<Node3D>());
            node.Position = EffectPosition(spawn.At, spawn.World);
            Orient(node, spawn.Direction ?? (spawn.To.HasValue ? spawn.To.Value - spawn.At : new(0, 0, -1)));
            var effect = new Live { Spawn = spawn, Root = node, Clock = new(definition, spawn.Size, spawn.Length,
                spawn.To.HasValue ? (spawn.To.Value - spawn.At).Length : null, spawn.Hold) };
            ConfigureMaterials(node, effect, spawn.Tint);
            live.Add(effect); while (live.Count > MaxLive) End(0, true);
        }
    }
    private void ConfigureMaterials(Node3D node, Live? effect, CombatPoint3? tint, string? item = null)
    {
        var duplicates = new Dictionary<BaseMaterial3D, BaseMaterial3D>();
        foreach (var mesh in Meshes(node))
        {
            SourceLightUnits.SetReceiver(mesh, SourceLightClass.ReferenceSurface, frame.RenderLayers, localCaster: false);
            mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            if (mesh.Mesh == null) continue;
            for (var index = 0; index < mesh.Mesh.GetSurfaceCount(); index++)
            {
                if ((mesh.MaterialOverride ?? mesh.GetSurfaceOverrideMaterial(index) ?? mesh.Mesh.SurfaceGetMaterial(index)) is not BaseMaterial3D original) continue;
                if (!duplicates.TryGetValue(original, out var material))
                {
                    material = (BaseMaterial3D)original.Duplicate(true); duplicates[original] = material;
                    if (effect != null) material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    if (tint is { } color && material.Emission.R * .2126 + material.Emission.G * .7152 + material.Emission.B * .0722 > 0)
                        material.Emission = SourceLightUnits.Colour(new((float)color.X, (float)color.Y, (float)color.Z));
                    if (item != null) ApplyThrownTheme(material, item);
                    effect?.Materials.Add((material, material.AlbedoColor, material.EmissionEnergyMultiplier));
                }
                mesh.SetSurfaceOverrideMaterial(index, material);
            }
            mesh.MaterialOverride = null;
        }
    }
    private static void ApplyThrownTheme(BaseMaterial3D material, string item)
    {
        var name = material.ResourceName; var split = name.IndexOf('@');
        var slot = name.StartsWith("slot:", StringComparison.Ordinal) ? name[5..(split < 0 ? name.Length : split)] : "";
        if (Catalog.ItemMaterials(item) is not { } table || !table.TryGetProperty(slot, out var value)) return;
        Color ColorOf(JsonElement array, float alpha = 1) => SourceLightUnits.Colour(new((float)array[0].GetDouble(), (float)array[1].GetDouble(), (float)array[2].GetDouble(), alpha));
        material.AlbedoColor = ColorOf(value.GetProperty("color"), value.TryGetProperty("alpha", out var alpha) ? (float)alpha.GetDouble() : 1);
        material.Roughness = (float)value.GetProperty("roughness").GetDouble(); material.Metallic = (float)value.GetProperty("metallic").GetDouble();
        if (value.TryGetProperty("emissive", out var emission)) { material.EmissionEnabled = true; material.Emission = ColorOf(emission); material.EmissionEnergyMultiplier = value.TryGetProperty("emissiveStrength", out var strength) ? (float)strength.GetDouble() : 1; }
    }
    private void End(int index, bool after)
    { var effect = live[index]; live.RemoveAt(index); Remove(effect.Root); if (after && effect.Spawn.After != null) Enqueue(effect.Spawn.After); }
    private void UpdateLive(double delta)
    {
        for (var index = live.Count - 1; index >= 0; index--)
        {
            var effect = live[index]; var sample = effect.Clock.Step(delta);
            effect.Root.Scale = Vector(sample.Scale);
            var point = effect.Spawn.To.HasValue ? CombatPoint3.Lerp(effect.Spawn.At, effect.Spawn.To.Value, effect.Clock.TravelFraction) : effect.Spawn.At;
            effect.Root.Position = EffectPosition(point, effect.Spawn.World);
            foreach (var material in effect.Materials)
            { var color = material.Albedo; color.A *= (float)sample.Opacity; material.Material.AlbedoColor = color; material.Material.EmissionEnergyMultiplier = material.Emission * (float)sample.Emissive; }
            if (sample.Finished) End(index, true);
        }
    }
    private static StandardMaterial3D LegacyMaterial(Color emission) => new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = Colors.Black, EmissionEnabled = true, Emission = SourceLightUnits.Colour(emission), Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
    private MeshInstance3D Primitive(Node3D parent, Mesh mesh, Material material)
    { var node = new MeshInstance3D { Mesh = mesh, MaterialOverride = material, Layers = frame.RenderLayers, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off }; parent.AddChild(node); return node; }
    private void Tracer(string actor, CombatPoint3 from, CombatPoint3 to, double now)
    {
        var root = new Node3D { Name = "AcceptedEvaTracer" }; ParentFor(true).AddChild(root);
        var effect = new Legacy { ActorId = actor, Start = now, Last = now, At = from, End = to, World = true, Root = root,
            Material = LegacyMaterial(new(.15f, 1, .35f)) };
        var mesh = Primitive(root, new BoxMesh { Size = Vector3.One }, effect.Material); mesh.Scale = new(.018f, .018f, (float)(to - from).Length);
        effect.Meshes.Add(mesh); Orient(root, to - from); legacy.Add(effect);
    }
    private void Impact(string actor, CombatPoint3 at, bool world, double now)
    {
        var root = new Node3D { Name = "AcceptedImpactFlash" }; ParentFor(world).AddChild(root);
        var effect = new Legacy { ActorId = actor, Start = now, Last = now, At = at, World = world, Impact = true, Root = root,
            Material = LegacyMaterial(new(.45f, 1, .55f)) };
        effect.Meshes.Add(Primitive(root, new SphereMesh { Radius = .5f, Height = 1, RadialSegments = 16, Rings = 7 }, effect.Material));
        for (var index = 0; index < 6; index++) { effect.Meshes.Add(Primitive(root, new BoxMesh { Size = Vector3.One * .035f }, effect.Material)); effect.SparkOffsets.Add(new(0, 0, 0)); }
        legacy.Add(effect); AcceptedImpacts++;
        while (legacy.Count > MaxLive) { Remove(legacy[0].Root); legacy.RemoveAt(0); }
    }
    private void UpdateLegacy(double now)
    {
        foreach (var effect in legacy.ToArray())
        {
            var age = Math.Max(0, now - effect.Start); var delta = Math.Clamp(now - effect.Last, 0, .1); effect.Last = now;
            var opacity = Math.Max(0, 1 - age / (effect.Impact ? CombatEffectProjection.ImpactLifetime : CombatEffectProjection.TracerLifetime));
            var color = effect.Material.AlbedoColor; color.A = (float)opacity; effect.Material.AlbedoColor = color;
            effect.Root.Position = EffectPosition(effect.Impact ? effect.At : CombatPoint3.Lerp(effect.At, effect.End, .5), effect.World);
            if (effect.Impact)
            {
                effect.Meshes[0].Scale = Vector3.One * (float)CombatEffectProjection.LegacyImpact(age).Scale;
                for (var index = 0; index < 6; index++) { effect.SparkOffsets[index] += CombatEffectProjection.SparkVelocity(index) * delta; effect.Meshes[index + 1].Position = Vector(effect.SparkOffsets[index]); }
            }
            if (opacity <= 0) { Remove(effect.Root); legacy.Remove(effect); }
        }
    }
    private void UpdateThrown(double now)
    {
        foreach (var effect in thrown.Values)
        {
            if (effect.Root == null)
            {
                var scene = Scene(effect.File, out var unavailable); if (scene == null) { if (unavailable) missing.Add(effect.File); continue; }
                effect.Root = scene.Instantiate<Node3D>(); effect.Root.Name = "AcceptedThrown_" + effect.ItemId; frame.ShipRoot.AddChild(effect.Root);
                ConfigureMaterials(effect.Root, null, null, effect.ItemId);
            }
            var seconds = Math.Max(0, now - effect.Start); effect.Root.Position = Vector(CombatEffectProjection.ThrownPosition(effect.From, effect.To, seconds));
            if (seconds < Math.Clamp((effect.To - effect.From).Length / 11, .25, 1.1)) effect.Rotation += .3;
            effect.Root.Rotation = new((float)effect.Rotation, 0, 0);
        }
    }
    private void DisposeDenied()
    {
        pending.RemoveAll(effect => !Allowed(effect.ActorId, effect.World));
        for (var index = live.Count - 1; index >= 0; index--) if (!Allowed(live[index].Spawn.ActorId, live[index].Spawn.World)) End(index, false);
        foreach (var effect in legacy.Where(effect => !Allowed(effect.ActorId, effect.World)).ToArray()) { Remove(effect.Root); legacy.Remove(effect); }
        foreach (var id in thrown.Keys.Where(id => !interiorActors.Contains(id)).ToArray()) { Remove(thrown[id].Root); thrown.Remove(id); }
    }
    private bool Allowed(string actor, bool world) => (world ? worldActors : interiorActors).Contains(actor);
}
