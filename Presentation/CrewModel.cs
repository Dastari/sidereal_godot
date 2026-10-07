using Godot;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Text.Json;
using FileAccess=Godot.FileAccess;

namespace Sidereal.Native;

public sealed record CrewAppearanceView(CrewLook Look)
{
    public static CrewAppearanceView FromCore(ClientCore core)
    {
        var inventory=core.Inventory;var equipment=inventory.Items.Where(i=>i.Definition!=null&&i.EquipmentSlot.Length>0&&i.EquipmentSlot!="hand").ToDictionary(i=>i.EquipmentSlot,i=>i.DefinitionId);
        var held=inventory.Items.FirstOrDefault(i=>i.Definition!=null&&i.EquipmentSlot=="hand");
        var item=held==null?null:CrewAssets.Catalog.DefinitionHeld(held.DefinitionId);
        // New published item revisions must use their disclosed payload's actual art mapping.
        if(held?.Definition is {Revision:>1} def)
        {
            var row=core.Connection?.Db.PublishedItemDefinitions.Iter().FirstOrDefault(r=>r.DefinitionId==def.Id&&r.Revision==def.Revision&&r.Kind=="item");
            item=null;if(row!=null){try{using var doc=JsonDocument.Parse(row.PayloadJson);item=CrewCatalog.Text(doc.RootElement,"crewItemId","");if(item.Length==0)item=null;}catch(JsonException){}}
        }
        return new(CrewAssets.Catalog.Resolve(core.Appearance?.AppearanceJson??"{}",equipment,item));
    }
    public static CrewAppearanceView FromRows(string appearance,string equipment)
    {
        var slots=CrewCatalog.Strings(equipment);var held=slots.Remove("hand",out var definition)?CrewAssets.Catalog.DefinitionHeld(definition):null;
        return new(CrewAssets.Catalog.Resolve(appearance,slots,held));
    }
}
public sealed record CrewMotionState(bool Moving=false,bool Sprinting=false,bool Seated=false,bool Dead=false,bool Aiming=false,
    ulong ShotSequence=0,ulong ReloadSequence=0,bool Eva=false,string EvaPhase="free",double Forward=0,double Strafe=0,double Turn=0,bool Walking=false,bool Cycling=false,double SpeedMultiplier=1)
{
    public static readonly CrewMotionState Preview=new();
}
public static class CrewAssets
{
    public const string Base="res://Assets/Crew/";
    private static CrewCatalog? catalog;
    private static Task<CrewAnimationBank>? bank;
    private static readonly Dictionary<string,CrewFaceAtlas> faces=new();
    private static readonly HashSet<string> requested=new();
    private static readonly Dictionary<string,PackedScene> scenes=new();
    private static readonly LinkedList<string> sceneLru=new();
    public static CrewCatalog Catalog=>catalog??=new(FileAccess.GetFileAsString(Base+"catalog.json"),FileAccess.GetFileAsString(Base+"semantics.json"));
    public static CrewAnimationBank? Animations
    {
        get
        {
            if(bank==null){var bytes=FileAccess.GetFileAsBytes(Base+CrewCatalog.Text(Catalog.Catalog.GetProperty("animation"),"file")+".bin");bank=Task.Run(()=>new CrewAnimationBank(bytes));}
            return bank.IsCompletedSuccessfully?bank.Result:null;
        }
    }
    public static string? AnimationError=>bank?.IsFaulted==true?"Crew animation release failed validation.":null;
    public static PackedScene? Scene(string file,out bool failed)
    {
        var path=Base+file;failed=false;
        if(scenes.TryGetValue(path,out var cached)){sceneLru.Remove(path);sceneLru.AddLast(path);return cached;}
        if(requested.Add(path))
        {
            var error=ResourceLoader.LoadThreadedRequest(path,"PackedScene");if(error!=Error.Ok){failed=true;return null;}
        }
        var status=ResourceLoader.LoadThreadedGetStatus(path);
        if(status==ResourceLoader.ThreadLoadStatus.Loaded)
        {
            var scene=ResourceLoader.LoadThreadedGet(path) as PackedScene;if(scene==null){failed=true;return null;}
            scenes[path]=scene;sceneLru.AddLast(path);
            // Only requested bodies/loadouts are retained, never every catalog GLB per actor.
            // Existing instances retain their own mesh resources when an unused scene leaves the LRU.
            while(scenes.Count>128&&sceneLru.First!=null){var oldest=sceneLru.First.Value;sceneLru.RemoveFirst();scenes.Remove(oldest);requested.Remove(oldest);}
            return scene;
        }
        failed=status is ResourceLoader.ThreadLoadStatus.Failed or ResourceLoader.ThreadLoadStatus.InvalidResource;return null;
    }
    public static CrewFaceAtlas Face(bool female)
    {
        var variant=female?"f_classic":"m_classic";if(faces.TryGetValue(variant,out var existing))return existing;
        var def=Catalog.Catalog.GetProperty("face").GetProperty(variant);var result=new CrewFaceAtlas(FileAccess.GetFileAsString(Base+CrewCatalog.Text(def,"json")),FileAccess.GetFileAsBytes(Base+CrewCatalog.Text(def,"png")+".bin"));faces[variant]=result;return result;
    }
}
/// <summary>The unchanged 32-joint released study body and worn assets, shared by world and paper doll.</summary>
public partial class CrewModel:Node3D
{
    private sealed record Part(CrewPartRequest Request,Node3D Root,List<Skeleton3D> Skeletons);
    private readonly Dictionary<string,Part> parts=new();
    private readonly Dictionary<string,string> wanted=new();
    private readonly List<Skeleton3D> skeletons=new();
    private readonly Dictionary<MeshInstance3D,Mesh> originalMeshes=new();
    private readonly Dictionary<MeshInstance3D,Material?[]> originalMaterials=new();
    private readonly List<BaseMaterial3D> faceMaterials=new();
    private Node3D visual=null!,body=null!,held=null!;private Skeleton3D? master;
    private bool rigValidated;
    private CrewAppearanceView? appearance;private string appearanceKey="",hairState="stand",candidateHair="stand";private double hairSince;
    private CrewSeatContact? seatContact;private string itemAction="";
    private string? heldId;private string heldPhase="empty";private double heldPhaseStarted,heldPhaseEnd,sightAmount;private bool initialHeld=true,actionFull;
    public string HeldPhase=>heldPhase;
    private readonly CrewFootPlanting footPlanting=new();private string footPoseKey="";private double footSettle,lastTick=double.NaN;
    public double FootPlantingError=>footPlanting.Error;
    public void SetSeatContact(CrewSeatContact? contact){seatContact=contact;if(visual!=null)visual.Position=new(0,0,-(float)(contact?.Forward??0));}
    private CrewBonePose[]? lastPose,blendFrom;private string lower="",upper="",oneShot="",lastFace="";private double layerStarted,blendStarted,blendDuration,oneShotStarted;private ulong? shotSequence,reloadSequence;
    private readonly HashSet<string> missing=new();private ulong generation;
    public uint RenderLayers {get;set;}=1;
    public bool ReducedMotion {get;set;}
    public new bool Ready=>rigValidated&&CrewAssets.Animations!=null;
    public int PendingCount {get;private set;}
    public string[] Unsupported=>missing.ToArray();
    public string[] ActiveClips=>oneShot.Length>0?(actionFull?new[]{oneShot}:new[]{lower,oneShot}):new[]{lower,upper}.Where(x=>x.Length>0).Distinct().ToArray();
    public ulong AppearanceGeneration=>generation;
    public override void _Ready(){EnsureVisual();}
    private void EnsureVisual()
    {
        if(visual!=null)return;visual=new Node3D{Name="StudyVisual",Scale=Godot.Vector3.One*(float)CrewAssets.Catalog.Scale};AddChild(visual);
    }
    public void SetAppearance(CrewAppearanceView next)
    {
        EnsureVisual();if(appearanceKey==next.Look.Key)return;var heldOnly=appearance!=null&&appearance.Look.OutfitKey==next.Look.OutfitKey;appearance=next;appearanceKey=next.Look.Key;generation++;missing.Clear();lastFace="";
        if(heldOnly)return;
        footPlanting.Reset();footPoseKey="";lastTick=double.NaN;
        oneShot="";
        // Remove old armor immediately; delayed loading never exposes a previous loadout.
        foreach(var p in parts.Values){p.Root.Visible=false;p.Root.QueueFree();}parts.Clear();wanted.Clear();
        if(held!=null){held.Visible=false;held.QueueFree();held=null!;}heldId=null;heldPhase="empty";initialHeld=true;sightAmount=0;
        if(body!=null){body.Visible=false;body.QueueFree();body=null!;master=null;rigValidated=false;skeletons.Clear();originalMeshes.Clear();originalMaterials.Clear();faceMaterials.Clear();subsets.Clear();}
    }
    public void Tick(CrewMotionState motion,double elapsed)
    {
        if(appearance==null)return;EnsureVisual();PendingCount=0;
        var dt=double.IsNaN(lastTick)?0:Math.Max(0,elapsed-lastTick);lastTick=elapsed;
        if(body==null)
        {
            var file=CrewCatalog.Text(CrewAssets.Catalog.Catalog.GetProperty("body"),"file");var scene=CrewAssets.Scene(file,out var failed);
            if(scene==null){if(failed)missing.Add(file);else PendingCount++;return;}
            body=scene.Instantiate<Node3D>();body.Visible=false;visual.AddChild(body);CollectSkeletons(body);master=skeletons.FirstOrDefault();PrepareMaterials(body,true);SaveMeshes(body);UpdateBodyCoverage();
            if(master==null){missing.Add("32-joint study skeleton");return;}
        }
        var bank=CrewAssets.Animations;if(bank==null){if(CrewAssets.AnimationError!=null)missing.Add(CrewAssets.AnimationError);else PendingCount++;return;}
        if(!rigValidated)
        {
            if(master==null||master.GetBoneCount()<bank.BoneNames.Length){missing.Add("32-joint imported study skeleton");return;}
            var absent=bank.BoneNames.Where(name=>FindBone(master,name)<0).ToArray();if(absent.Length>0){foreach(var name in absent)missing.Add("joint:"+name);return;}rigValidated=true;
        }
        body.Visible=true;UpdateParts(motion,elapsed);UpdateHeld(motion,elapsed);var selected=SelectLayers(motion,heldId,bank);
        if(selected.Lower!=lower||selected.Upper!=upper)
        {
            blendFrom=lastPose;blendStarted=elapsed;blendDuration=ReducedMotion?0:BlendDuration(lower+upper,selected.Lower+selected.Upper);lower=selected.Lower;upper=selected.Upper;layerStarted=elapsed;
        }
        var pose=bank.Sample(lower,ReducedMotion?0:(elapsed-layerStarted)*selected.Speed,Loops(lower));
        if(upper.Length>0)
        {
            var upperSpeed=selected.UpperSync?selected.Speed*bank.Duration(upper)/Math.Max(.001,bank.Duration(lower)):1;
            var upperPose=bank.Sample(upper,ReducedMotion?0:(elapsed-layerStarted)*upperSpeed,Loops(upper));for(var i=0;i<pose.Length;i++)if(CrewAssets.Catalog.UpperBones.Contains(bank.BoneNames[i]))pose[i]=upperPose[i];
        }
        if(!motion.Dead&&!motion.Seated)
        {
            if(shotSequence.HasValue&&shotSequence!=motion.ShotSequence)StartAction(ArmedAction("shoot",heldId,bank)??(heldId!=null?"shoot_pistol":""),elapsed);
            if(reloadSequence.HasValue&&reloadSequence!=motion.ReloadSequence)StartAction(ArmedAction("reload",heldId,bank)??"reload",elapsed);
        }
        else oneShot="";
        shotSequence=motion.ShotSequence;reloadSequence=motion.ReloadSequence;
        if(oneShot.Length>0)
        {
            var time=elapsed-oneShotStarted;if(time>=bank.Duration(oneShot)){blendFrom=lastPose;blendStarted=elapsed;blendDuration=ReducedMotion?0:BlendDuration(oneShot,lower+upper);oneShot="";}else
            {
                var action=bank.Sample(oneShot,ReducedMotion?0:time,false);actionFull=!oneShot.Contains("shoot")&&!motion.Moving;var weight=ReducedMotion?1:(float)Math.Min(1,time/Math.Max(.001,BlendDuration(lower+upper,oneShot)));
                for(var i=0;i<pose.Length;i++)if(actionFull||CrewAssets.Catalog.UpperBones.Contains(bank.BoneNames[i]))pose[i]=CrewBonePose.Blend(pose[i],action[i],weight);
            }
        }
        if(blendFrom!=null&&elapsed-blendStarted<blendDuration){var weight=(float)Math.Clamp((elapsed-blendStarted)/blendDuration,0,1);weight=weight*weight*(3-2*weight);for(var i=0;i<pose.Length;i++)pose[i]=CrewBonePose.Blend(blendFrom[i],pose[i],weight);}
        if(motion.Seated&&!motion.Dead&&seatContact is { } contact)CrewKinematics.Seat(bank,pose,CrewAssets.Catalog.Scale,contact.Lift,contact.Lean,contact.FootSupport,contact.Forward,contact.FootForward);
        else if(!motion.Seated&&!motion.Dead&&!motion.Eva)
        {
            var fullAction=oneShot.Length>0&&!oneShot.Contains("shoot")&&!motion.Moving;var key=fullAction?oneShot:lower;
            if(key!=footPoseKey){footPoseKey=key;footSettle=blendDuration;footPlanting.Reset();}
            footSettle=Math.Max(0,footSettle-dt);
            var frame=Transform*visual.Transform;var x=frame.Basis.X;var y=frame.Basis.Y;var z=frame.Basis.Z;var p=frame.Origin;
            var modelToFrame=new System.Numerics.Matrix4x4(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,p.X,p.Y,p.Z,1);
            footPlanting.Step(bank,pose,modelToFrame,dt,footSettle==0);
        }
        else footPlanting.Reset();
        lastPose=pose;ApplyPose(bank,pose);UpdateGrip();UpdateItemParts(elapsed,dt);UpdateFace(elapsed);UpdateHairState(motion,elapsed);
    }
    private void StartAction(string clip,double elapsed){if(clip.Length==0||CrewAssets.Animations?.Has(clip)!=true)return;oneShot=clip;oneShotStarted=elapsed;itemAction="";}
    private static string? ArmedAction(string action,string? held,CrewAnimationBank bank){var cls=CrewAssets.Catalog.ArmedClass(held);var name=cls+"."+action;return cls!=null&&bank.Has(name)?name:null;}
    private (string Lower,string Upper,double Speed,bool UpperSync) SelectLayers(CrewMotionState motion,string? item,CrewAnimationBank bank)
    {
        string Resolve(string name){if(bank.Has(name))return name;if(CrewAssets.Catalog.Semantics.GetProperty("clipFallback").TryGetProperty(name,out var fallback)&&bank.Has(fallback.GetString()!))return fallback.GetString()!;missing.Add("clip:"+name);return "idle";}
        double Rate(string clip,double speed)=>CrewAssets.Catalog.Catalog.GetProperty("clips").TryGetProperty(clip,out var info)&&info.TryGetProperty("nominalSpeed",out var nominal)&&nominal.GetDouble()>0?speed/CrewAssets.Catalog.Scale:speed;
        if(motion.Dead)return(Resolve("death"),"",1,false);
        if(motion.Eva){var name=motion.Cycling?"Maglock_Idle":motion.EvaPhase=="maglocked"?motion.Walking?"Maglock_Walk":"Maglock_Idle":motion.Forward>.3?"ZeroG_Flight":Math.Abs(motion.Strafe)>.1||motion.Forward<-.1?"ZeroG_Locomotion_Prone":Math.Abs(motion.Turn)>.1?"ZeroG_Swim":"ZeroG_Prone";return(Resolve(name),"",Rate(name,1),false);}
        if(motion.Seated)return(Resolve("sit_idle"),"",1,false);
        var legs=motion.Moving?motion.Sprinting?"run":"walk":"idle";var speed=Rate(legs,CrewAssets.Catalog.SpeedRatio(legs,motion.SpeedMultiplier));var cls=CrewAssets.Catalog.ArmedClass(item);
        if(cls==null)return(Resolve(legs),"",speed,false);
        var aim=motion.Aiming&&!motion.Sprinting?ArmedAction("aim",item,bank):null;
        if(motion.Moving){var upper=aim??ArmedAction(motion.Sprinting?"run_armed":"walk_armed",item,bank)??ArmedAction("walk_armed",item,bank);return(Resolve(legs),upper??"",speed,aim==null);}
        if(aim!=null)return(aim,"",1,false);
        return("idle",ArmedAction("idle_armed",item,bank)??"",1,false);
    }
    private bool Loops(string name)=>CrewAssets.Catalog.Catalog.GetProperty("clips").TryGetProperty(name,out var clip)&&clip.GetProperty("loop").GetBoolean();
    private static double BlendDuration(string a,string b)=>a==b?0:Regex.IsMatch(a+b,"ZeroG|Maglock|jetpack")?.25:Regex.IsMatch(a+b,"sit|death|knocked|revive")?.3:b.Contains("shoot")?.04:Regex.IsMatch(a+b,"aim|armed|pistol")?.16:.2;
    private void UpdateParts(CrewMotionState motion,double elapsed)
    {
        var requests=CrewAssets.Catalog.Parts(appearance!.Look,hairState);var keys=requests.Select(p=>p.Slot).ToHashSet();
        foreach(var key in parts.Keys.Where(k=>!keys.Contains(k)).ToArray()){ForgetPart(parts[key]);parts.Remove(key);}
        foreach(var request in requests)
        {
            var key=request.File+":"+string.Join(",",request.Regions??Array.Empty<string>());
            if(parts.TryGetValue(request.Slot,out var p)&&wanted.GetValueOrDefault(request.Slot)==key){p.Root.Visible=request.Slot!="back"||!motion.Seated;continue;}
            if(p!=null){ForgetPart(p);parts.Remove(request.Slot);}wanted[request.Slot]=key;
            var scene=CrewAssets.Scene(request.File,out var failed);if(scene==null){if(failed)missing.Add(request.File);else PendingCount++;continue;}
            var root=scene.Instantiate<Node3D>();visual.AddChild(root);var sk=Descendants<Skeleton3D>(root).ToList();PrepareMaterials(root,false,CrewAssets.Catalog.Catalog.GetProperty("parts").GetProperty(request.Id));SaveMeshes(root);
            foreach(var mesh in Descendants<MeshInstance3D>(root))if(request.Regions!=null)SelectRegions(mesh,r=>request.Regions.Contains(r));
            root.Visible=request.Slot!="back"||!motion.Seated;parts[request.Slot]=new(request,root,sk);
        }
        UpdateBodyCoverage();
    }
    private void ForgetPart(Part part)
    {
        part.Root.Visible=false;foreach(var mesh in Descendants<MeshInstance3D>(part.Root)){if(originalMeshes.Remove(mesh,out var source))foreach(var key in subsets.Keys.Where(k=>k.Item1==source).ToArray())subsets.Remove(key);originalMaterials.Remove(mesh);}part.Root.QueueFree();
    }
    private void UpdateHeld(CrewMotionState motion,double elapsed)
    {
        var wantedId=appearance!.Look.HeldItem;
        if(held!=null&&heldId!=wantedId&&heldPhase!="holstering")
        {
            var clip=ArmedAction("holster",heldId,CrewAssets.Animations!);
            if(clip!=null&&!ReducedMotion&&!motion.Dead&&!motion.Seated)
            {heldPhase="holstering";heldPhaseStarted=elapsed;heldPhaseEnd=CrewAssets.Animations!.Duration(clip);StartAction(clip,elapsed);}
            else DisposeHeld();
        }
        if((heldPhase is "drawing" or "holstering")&&(ReducedMotion||elapsed-heldPhaseStarted>=heldPhaseEnd))
        {if(heldPhase=="holstering")DisposeHeld();else heldPhase="held";}
        if(held!=null)return;if(wantedId==null){initialHeld=false;return;}var file=CrewAssets.Catalog.HeldFile(wantedId);if(file==null){missing.Add("held:"+wantedId);return;}
        var scene=CrewAssets.Scene(file,out var failed);if(scene==null){if(failed)missing.Add(file);else PendingCount++;return;}
        held=scene.Instantiate<Node3D>();visual.AddChild(held);heldId=wantedId;heldPhase="held";itemAction="";sightAmount=0;
        PrepareMaterials(held,false,CrewAssets.Catalog.Catalog.GetProperty("items").GetProperty(CrewCatalog.StudyItemKey(wantedId)));
        var draw=ArmedAction("draw",wantedId,CrewAssets.Animations!);
        if(!initialHeld&&draw!=null&&!ReducedMotion&&!motion.Dead&&!motion.Seated)
        {heldPhase="drawing";heldPhaseStarted=elapsed;heldPhaseEnd=CrewAssets.Animations!.Duration(draw);StartAction(draw,elapsed);}
        initialHeld=false;
    }
    private void DisposeHeld()
    {if(held!=null){held.Visible=false;held.QueueFree();held=null!;}heldId=null;heldPhase="empty";itemAction="";sightAmount=0;}
    private void UpdateItemParts(double elapsed,double dt)
    {
        if(held==null)return;var requested=oneShot.Contains("reload")?"reload":oneShot.Contains("shoot")?"fire":"RESET";
        var deployed=ActiveClips.Any(clip=>CrewAssets.Catalog.Catalog.GetProperty("clips").TryGetProperty(clip,out var info)&&CrewCatalog.Text(info,"sight")=="deployed");
        var target=deployed?1d:0d;sightAmount+=Math.Sign(target-sightAmount)*Math.Min(Math.Abs(target-sightAmount),Math.Min(.1,dt)/.25);
        foreach(var player in Descendants<AnimationPlayer>(held))
        {
            player.CallbackModeProcess=AnimationMixer.AnimationCallbackModeProcess.Manual;
            var names=player.GetAnimationList();var name=names.FirstOrDefault(n=>n==requested||n.EndsWith("/"+requested,StringComparison.Ordinal));
            if(name!=null)
            {player.Play(name,0,1);player.Seek(requested=="RESET"?0:Math.Clamp(elapsed-oneShotStarted,0,player.GetAnimation(name).Length),true);}
            var sight=names.FirstOrDefault(n=>n=="deploy_sight"||n.EndsWith("/deploy_sight",StringComparison.Ordinal));
            if(sight!=null){player.Play(sight,0,1);player.Seek(player.GetAnimation(sight).Length*sightAmount,true);}
        }
        itemAction=requested;
    }
    private void UpdateGrip()
    {
        if(held==null||master==null)return;var bone=FindBone(master,"prop.R");if(bone<0){held.Visible=false;missing.Add("prop.R item mount");return;}
        held.Transform=visual.GlobalTransform.AffineInverse()*master.GlobalTransform*master.GetBoneGlobalPose(bone);held.Visible=true;
    }
    private void CollectSkeletons(Node root){skeletons.Clear();skeletons.AddRange(Descendants<Skeleton3D>(root));}
    private static int FindBone(Skeleton3D skeleton,string name){var index=skeleton.FindBone(name);return index>=0?index:skeleton.FindBone(name.Replace('.','_'));}
    private void ApplyPose(CrewAnimationBank bank,CrewBonePose[] pose)
    {
        foreach(var skeleton in skeletons.Concat(parts.Values.SelectMany(p=>p.Skeletons)))
        {
            if(!GodotObject.IsInstanceValid(skeleton))continue;
            for(var i=0;i<bank.BoneNames.Length;i++)
            {
                var bone=FindBone(skeleton,bank.BoneNames[i]);if(bone<0){missing.Add("joint:"+bank.BoneNames[i]);continue;}var p=pose[i];
                if(hairState!="stand"&&bank.BoneNames[i].StartsWith("hair."))p=bank.Rest[i];
                skeleton.SetBonePosePosition(bone,new(p.Position.X,p.Position.Y,p.Position.Z));skeleton.SetBonePoseRotation(bone,new(p.Rotation.X,p.Rotation.Y,p.Rotation.Z,p.Rotation.W));skeleton.SetBonePoseScale(bone,new(p.Scale.X,p.Scale.Y,p.Scale.Z));
            }
        }
    }
    private void SaveMeshes(Node root){foreach(var mesh in Descendants<MeshInstance3D>(root)){if(mesh.Mesh!=null){originalMeshes[mesh]=mesh.Mesh;originalMaterials[mesh]=Enumerable.Range(0,mesh.Mesh.GetSurfaceCount()).Select(i=>mesh.GetSurfaceOverrideMaterial(i)??mesh.Mesh.SurfaceGetMaterial(i)).ToArray();}SourceLightUnits.SetReceiver(mesh,SourceLightClass.Crew,RenderLayers);}}
    private void UpdateBodyCoverage()
    {
        if(body==null||appearance==null)return;var cover=parts.Values.SelectMany(p=>p.Request.Covers).ToHashSet();
        foreach(var mesh in Descendants<MeshInstance3D>(body))
        {
            var name=mesh.Name.ToString();var female=name.Contains("female");mesh.Visible=female==appearance.Look.Female;
            if(name.Contains("head_low"))mesh.Visible&=cover.Contains("scalp")&&!cover.Contains("head");
            else if(name.Contains("head"))mesh.Visible&=!cover.Contains("scalp")&&!cover.Contains("head");
            if(name.Contains("hands"))mesh.Visible&=!cover.Contains("hands");
            if(name.Contains("base"))SelectRegions(mesh,r=>!cover.Contains(r));
        }
    }
    private readonly Dictionary<(Mesh,string),Mesh> subsets=new();
    private void SelectRegions(MeshInstance3D mesh,Func<string,bool> keep)
    {
        if(!originalMeshes.TryGetValue(mesh,out var source))return;var regions=new[]{"neck","torso","hips","upperArms","forearms","legs","feet","unclassified"}.Where(keep).ToHashSet();var key=string.Join(",",regions.Order());
        if(subsets.TryGetValue((source,key),out var existing)){SetSubset(mesh,existing);return;}var result=new ArrayMesh();
        for(var surface=0;surface<source.GetSurfaceCount();surface++)
        {
            var arrays=source.SurfaceGetArrays(surface);var vertices=arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();var indices=arrays[(int)Mesh.ArrayType.Index].AsInt32Array();if(indices.Length==0)indices=Enumerable.Range(0,vertices.Length).ToArray();
            var joints=arrays[(int)Mesh.ArrayType.Bones].VariantType==Variant.Type.Nil?Array.Empty<int>():arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();var weights=arrays[(int)Mesh.ArrayType.Weights].VariantType==Variant.Type.Nil?Array.Empty<float>():arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            var boneWidth=vertices.Length==0?4:joints.Length/vertices.Length;var boundSkeleton=mesh.GetNodeOrNull<Skeleton3D>(mesh.Skeleton);
            string Region(int vertex){for(var c=0;c<boneWidth;c++)if(vertex*boneWidth+c<weights.Length&&weights[vertex*boneWidth+c]>.999){var bind=joints[vertex*boneWidth+c];var name=mesh.Skin!=null&&bind<mesh.Skin.GetBindCount()?mesh.Skin.GetBindName(bind).ToString():"";if(name.Length==0&&mesh.Skin!=null&&bind<mesh.Skin.GetBindCount()&&boundSkeleton!=null){var bone=mesh.Skin.GetBindBone(bind);if(bone>=0&&bone<boundSkeleton.GetBoneCount())name=boundSkeleton.GetBoneName(bone).ToString();}return CrewAssets.Catalog.BoneRegion(name.EndsWith("_R")||name.EndsWith("_L")?name[..^2]+"."+name[^1]:name);}return "unclassified";}
            var selected=new List<int>();for(var i=0;i+2<indices.Length;i+=3){var r=Region(indices[i]);if(r!=Region(indices[i+1])||r!=Region(indices[i+2]))r="unclassified";if(regions.Contains(r))selected.AddRange(new[]{indices[i],indices[i+1],indices[i+2]});}
            if(selected.Count==0)continue;var copy=(Godot.Collections.Array)arrays.Duplicate();copy[(int)Mesh.ArrayType.Index]=selected.ToArray();result.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,copy);result.SurfaceSetMaterial(result.GetSurfaceCount()-1,originalMaterials[mesh][surface]);
        }
        subsets[(source,key)]=result;SetSubset(mesh,result);if(result.GetSurfaceCount()==0)mesh.Visible=false;
    }
    private static void SetSubset(MeshInstance3D mesh,Mesh subset)
    {
        mesh.Mesh=subset;
        // Masked surfaces can change indices. Preserve each surviving surface's own
        // authored material rather than inheriting the old override at its new index.
        for(var i=0;i<subset.GetSurfaceCount();i++)mesh.SetSurfaceOverrideMaterial(i,subset.SurfaceGetMaterial(i));
    }
    private void PrepareMaterials(Node root,bool bodyMaterial,JsonElement? definition=null)
    {
        foreach(var mesh in Descendants<MeshInstance3D>(root))
        {
            SourceLightUnits.SetReceiver(mesh,SourceLightClass.Crew,RenderLayers);if(mesh.Mesh==null)continue;
            for(var i=0;i<mesh.Mesh.GetSurfaceCount();i++)
            {
                if(mesh.Mesh.SurfaceGetMaterial(i) is not BaseMaterial3D original)continue;var material=(BaseMaterial3D)original.Duplicate();var match=Regex.Match(material.ResourceName,@"^crew\.([a-z_]+)|^slot:([a-z_]+)@");var slot=match.Groups[1].Value+match.Groups[2].Value;
                string color="";if(slot is "skin" or "hair")color=appearance!.Look.Get(slot);
                if(bodyMaterial){color=slot switch{"suit_primary"=>appearance!.Look.Get("suit"),"accent"=>appearance!.Look.Get("accent"),"metal"=>appearance!.Look.Get("trim"),"emit"=>appearance!.Look.Get("light"),"glass"=>appearance!.Look.Get("visor"),_=>color};}
                if(Regex.IsMatch(color,@"^#[0-9a-fA-F]{6}$"))material.AlbedoColor=SourceLightUnits.ManualHexColour(color);
                if(slot.StartsWith("emit")){material.EmissionEnergyMultiplier=.9f;material.Emission=material.AlbedoColor;}
                var family=SourceSurfaceFinish.CrewFamily(slot,bodyMaterial);
                if(definition is { } def&&def.TryGetProperty("families",out var families)&&families.TryGetProperty(slot,out var authored))family=authored.GetString() switch{"plastic" or "hair"=>"plastic-colour","plastic_dark" or "visor"=>"plastic-dark","cloth" or "fabric"=>"fabric","emit" or "emissive"=>"emissive",var named=>named};
                SourceSurfaceFinish.Apply(material,family);
                material.TextureFilter=BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;mesh.SetSurfaceOverrideMaterial(i,material);if(slot=="face")faceMaterials.Add(material);
            }
        }
    }
    private void UpdateFace(double elapsed)
    {
        var face=CrewAssets.Face(appearance!.Look.Female);var expression=appearance.Look.Get("expression","neutral");var clip=oneShot.Length>0?oneShot:upper.Length>0?upper:lower;
        if(CrewAssets.Catalog.Catalog.GetProperty("clips").TryGetProperty(clip,out var meta)&&meta.TryGetProperty("expressionTrack",out var expressions))
        {
            var frame=(elapsed-(oneShot.Length>0?oneShotStarted:layerStarted))*meta.GetProperty("fps").GetDouble();foreach(var e in expressions.EnumerateArray())if(frame>=e[0].GetDouble())expression=e[1].GetString()!;
        }
        var blink=face.Blink(elapsed);var key=appearanceKey+expression+blink;if(lastFace==key)return;lastFace=key;var image=Image.CreateFromData(face.Cell,face.Cell,false,Image.Format.Rgba8,face.Compose(appearance.Look,expression,blink));var texture=ImageTexture.CreateFromImage(image);
        foreach(var material in faceMaterials.Where(GodotObject.IsInstanceValid)){material.AlbedoColor=Colors.White;material.AlbedoTexture=texture;}
    }
    private void UpdateHairState(CrewMotionState motion,double elapsed)
    {
        var next=motion.Seated?"seated":"stand";if(motion.Dead&&master!=null){var chest=FindBone(master,"chest");if(chest>=0){var basis=(master.GlobalTransform*master.GetBoneGlobalPose(chest)).Basis;if(Math.Abs(basis.Y.Y)<.35)next=Math.Abs((-basis.Z).Y)>.6?"lying":(-basis.Z).X>0?"lying_r":"lying_l";}}
        if(next!=candidateHair){candidateHair=next;hairSince=elapsed;}else if(next!=hairState&&elapsed-hairSince>=.1){hairState=next;}
    }
    private static IEnumerable<T> Descendants<T>(Node root)where T:Node{if(root is T value)yield return value;foreach(var child in root.GetChildren())foreach(var found in Descendants<T>(child))yield return found;}
}
