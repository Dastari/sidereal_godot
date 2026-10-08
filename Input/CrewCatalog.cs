using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Numerics;
using System.Buffers.Binary;

namespace Sidereal.Native;

public sealed record CrewPartRequest(string Slot,string Id,string File,string[]? Regions,string Mode,string State,string[] Covers);
public sealed record CrewLook(IReadOnlyDictionary<string,string> Values,IReadOnlyDictionary<string,string> Equipment,string? HeldItem)
{
    public string Get(string key,string fallback="") => Values.TryGetValue(key,out var value)?value:fallback;
    public bool Female => Get("bodyType")=="female";
    public string OutfitKey => string.Join("|",Values.OrderBy(x=>x.Key).Select(x=>x.Key+"="+x.Value))+";"+string.Join("|",Equipment.OrderBy(x=>x.Key).Select(x=>x.Key+"="+x.Value));
    public string Key => OutfitKey+";"+HeldItem;
}
public sealed class CrewCatalog
{
    public JsonElement Catalog { get; }
    public JsonElement Semantics { get; }
    public double Scale => Semantics.GetProperty("scale").GetDouble();
    public IReadOnlySet<string> UpperBones {get;}
    public CrewCatalog(string catalog,string semantics)
    {
        using var a=JsonDocument.Parse(catalog);using var b=JsonDocument.Parse(semantics);
        Catalog=a.RootElement.Clone();Semantics=b.RootElement.Clone();
        if(Text(Catalog,"revision")!="study-v2-r001" || Scale!=.9)throw new InvalidOperationException("Unsupported crew presentation release.");
        UpperBones=Semantics.GetProperty("upperBones").EnumerateArray().Select(x=>x.GetString()!).ToHashSet();
    }
    public static string Text(JsonElement element,string key,string fallback="") => element.ValueKind==JsonValueKind.Object&&element.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??fallback:fallback;
    public static Dictionary<string,string> Strings(string json)
    {
        try {using var d=JsonDocument.Parse(json);return d.RootElement.ValueKind==JsonValueKind.Object?d.RootElement.EnumerateObject().Where(p=>p.Value.ValueKind==JsonValueKind.String).ToDictionary(p=>p.Name,p=>p.Value.GetString()!):new();}
        catch(JsonException){return new();}
    }
    public CrewLook Resolve(string json,IReadOnlyDictionary<string,string> equipment,string? held=null)
    {
        var input=Strings(json);var outfit=input.GetValueOrDefault("outfit","engineer");
        if(!Semantics.GetProperty("defaults").TryGetProperty(outfit,out var defaults))defaults=Semantics.GetProperty("defaults").GetProperty("engineer");
        var values=defaults.EnumerateObject().Where(x=>x.Value.ValueKind==JsonValueKind.String).ToDictionary(x=>x.Name,x=>x.Value.GetString()!);
        foreach(var p in input)values[p.Key]=p.Value;
        return new(values,new Dictionary<string,string>(equipment),held);
    }
    public string? HeldFile(string? item)
    {
        if(item==null)return null;
        return Catalog.GetProperty("items").TryGetProperty(StudyItemKey(item),out var p)?Text(p.GetProperty("files").GetProperty("lod0"),"file"):null;
    }
    public static string StudyItemKey(string item)=>"item."+(item=="medkit"?"med_kit":item.Replace('-','_'));
    public string? ArmedClass(string? item) => item!=null&&Semantics.GetProperty("armedClasses").TryGetProperty(item,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
    public string? DefinitionHeld(string definition) => Semantics.GetProperty("definitions").TryGetProperty(definition,out var d)?Text(d,"crewItemId","") is {Length:>0} id?id:null:null;
    public IReadOnlyList<CrewPartRequest> Parts(CrewLook look,string state="stand")
    {
        var wanted=new Dictionary<string,(string Id,string[]? Regions)>();
        foreach(var p in look.Equipment)
            if(Semantics.GetProperty("equipment").TryGetProperty(p.Value,out var e)&&Text(e,"slot")==p.Key)
                wanted[p.Key]=(Text(e,"id"),e.TryGetProperty("regions",out var r)?r.EnumerateArray().Select(x=>x.GetString()!).ToArray():null);
        if(look.Equipment.GetValueOrDefault("uniform")=="wardrobe-suit-body"&&!wanted.ContainsKey("gloves"))wanted["gloves"]=("gloves.flight",null);
        if(wanted.ContainsKey("uniform"))foreach(var slot in new[]{"chest","legs"})if(wanted.TryGetValue(slot,out var p)&&p.Id.StartsWith("uniform."))wanted.Remove(slot);
        if(!wanted.ContainsKey("uniform"))
        {
            if(wanted.TryGetValue("chest",out var p)&&p.Id.StartsWith("armor.chest."))wanted["chest-cloth"]=(p.Id=="armor.chest.t3"?"uniform.marine":"uniform.civilian",new[]{"torso","upperArms","forearms"});
            if(wanted.GetValueOrDefault("legs").Id=="armor.legs.t3")wanted["legs-cloth"]=("uniform.marine",new[]{"hips","legs"});
        }
        var ranks=new[]{"full","cap","fringe","hidden"};var mode="full";var parts=Catalog.GetProperty("parts");
        foreach(var p in wanted.Values)if(parts.TryGetProperty(p.Id,out var def)){var candidate=Text(def,"hair_mode","full");if(Array.IndexOf(ranks,candidate)>Array.IndexOf(ranks,mode))mode=candidate;}
        var hair=look.Get("hairStyle","swept");if(!Semantics.GetProperty("hair").TryGetProperty(hair,out var pair))pair=Semantics.GetProperty("hair").GetProperty("swept");
        if(mode!="hidden"&&pair[look.Female?1:0].ValueKind==JsonValueKind.String)wanted["hair"]=(pair[look.Female?1:0].GetString()!,null);
        var facial=look.Get("facialHair","none");if(!look.Female&&mode!="hidden"&&facial!="none"&&parts.TryGetProperty("facial."+facial,out _))wanted["facial"]=("facial."+facial,null);
        var result=new List<CrewPartRequest>();
        foreach(var w in wanted)
        {
            if(!parts.TryGetProperty(w.Value.Id,out var def))continue;
            var partMode=w.Key=="hair"?mode:"full";var partState=w.Key=="hair"?state:"stand";var slot=Text(def,"slot");
            var suffix=slot is "hair" or "facial"?"#"+partMode:"";var pose=partState=="stand"?"":"@"+partState;var fit=look.Female?"narrow":"wide";
            var files=def.GetProperty("files");JsonElement file=default;
            foreach(var key in new[]{fit+suffix+pose,"all"+suffix+pose,fit+suffix,"all"+suffix})if(files.TryGetProperty(key,out file))break;
            if(file.ValueKind!=JsonValueKind.Object)continue;
            var covers=def.GetProperty("covers").EnumerateArray().Select(x=>x.GetString()!).ToList();
            if(def.TryGetProperty("covers_by_mode",out var cm)&&cm.TryGetProperty(partMode,out var pc))covers.AddRange(pc.EnumerateArray().Select(x=>x.GetString()!));
            if(w.Value.Regions!=null)covers=covers.Where(w.Value.Regions.Contains).ToList();
            result.Add(new(w.Key,w.Value.Id,Text(file,"file"),w.Value.Regions,partMode,partState,covers.Distinct().ToArray()));
        }
        return result;
    }
    public string BoneRegion(string name)
    {
        if(name=="neck")return "neck";if(name is "spine" or "chest")return "torso";if(name=="pelvis")return "hips";
        if(name.StartsWith("shoulder.")||name.StartsWith("upper_arm."))return "upperArms";if(name.StartsWith("forearm."))return "forearms";
        if(name.StartsWith("thigh.")||name.StartsWith("shin."))return "legs";if(name.StartsWith("foot.")||name.StartsWith("toe."))return "feet";return "unclassified";
    }
    public double SpeedRatio(string clip,double multiplier=1)
    {
        var speeds=Semantics.GetProperty("nominalSpeeds");if(!speeds.TryGetProperty(clip,out var v)||v.GetDouble()<=0)return 1;
        var speed=Semantics.GetProperty(clip=="run"?"sprintSpeed":"walkSpeed").GetDouble();return Math.Clamp(speed*(clip is "crouch_walk" or "carry_walk"?.6:1)/v.GetDouble()*multiplier,.5,2);
    }
}
public readonly record struct CrewBonePose(Vector3 Position,Quaternion Rotation,Vector3 Scale)
{
    public static CrewBonePose Blend(CrewBonePose a,CrewBonePose b,float weight)=>new(Vector3.Lerp(a.Position,b.Position,weight),Quaternion.Normalize(Quaternion.Slerp(a.Rotation,b.Rotation,weight)),Vector3.Lerp(a.Scale,b.Scale,weight));
}
public sealed class CrewAnimationBank
{
    private sealed record Track(int Bone,string Path,float[] Times,float[] Values,int Stride);
    private sealed record Clip(string Name,double Duration,List<Track> Tracks);
    private readonly Dictionary<string,Clip> clips=new();
    public string[] BoneNames {get;}
    public CrewBonePose[] Rest {get;}
    public int[] Parents {get;}
    public int Count=>clips.Count;
    public CrewAnimationBank(byte[] bytes)
    {
        if(bytes.Length<20||BinaryPrimitives.ReadUInt32LittleEndian(bytes)!=0x46546c67||BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8))!=bytes.Length)throw new InvalidOperationException("Invalid crew animation GLB.");
        var jsonSize=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));using var doc=JsonDocument.Parse(bytes.AsMemory(20,jsonSize));var d=doc.RootElement;
        var binaryOffset=20+jsonSize+8;var nodes=d.GetProperty("nodes");var joints=d.GetProperty("skins")[0].GetProperty("joints").EnumerateArray().Select(x=>x.GetInt32()).ToArray();
        BoneNames=joints.Select(x=>CrewCatalog.Text(nodes[x],"name")).ToArray();Parents=Enumerable.Repeat(-1,joints.Length).ToArray();for(var n=0;n<nodes.GetArrayLength();n++)if(nodes[n].TryGetProperty("children",out var children))foreach(var child in children.EnumerateArray()){var bone=System.Array.IndexOf(joints,child.GetInt32());if(bone>=0)Parents[bone]=System.Array.IndexOf(joints,n);}if(BoneNames.Length!=32||BoneNames.Distinct().Count()!=32)throw new InvalidOperationException("Wrong crew skeleton.");
        float[] Array(JsonElement n,string key,float[] fallback)=>n.TryGetProperty(key,out var v)?v.EnumerateArray().Select(x=>x.GetSingle()).ToArray():fallback;
        Rest=joints.Select(x=>{var n=nodes[x];var t=Array(n,"translation",new float[]{0,0,0});var r=Array(n,"rotation",new float[]{0,0,0,1});var s=Array(n,"scale",new float[]{1,1,1});return new CrewBonePose(new(t[0],t[1],t[2]),new(r[0],r[1],r[2],r[3]),new(s[0],s[1],s[2]));}).ToArray();
        var accesses=new Dictionary<int,float[]>();
        float[] Accessor(int id)
        {
            if(accesses.TryGetValue(id,out var existing))return existing;
            var a=d.GetProperty("accessors")[id];if(a.GetProperty("componentType").GetInt32()!=5126||a.TryGetProperty("sparse",out _))throw new InvalidOperationException("Unsupported crew animation accessor.");
            var type=CrewCatalog.Text(a,"type");var components=type switch{"SCALAR"=>1,"VEC3"=>3,"VEC4"=>4,_=>throw new InvalidOperationException("Unsupported crew channel.")};
            var view=d.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];var start=binaryOffset+(view.TryGetProperty("byteOffset",out var vo)?vo.GetInt32():0)+(a.TryGetProperty("byteOffset",out var ao)?ao.GetInt32():0);
            var count=a.GetProperty("count").GetInt32();var stride=view.TryGetProperty("byteStride",out var st)?st.GetInt32():components*4;var values=new float[count*components];
            for(var i=0;i<count;i++)for(var j=0;j<components;j++)values[i*components+j]=BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(start+i*stride+j*4,4)));
            if(values.Any(x=>!float.IsFinite(x)))throw new InvalidOperationException("Nonfinite crew animation.");accesses[id]=values;return values;
        }
        foreach(var animation in d.GetProperty("animations").EnumerateArray())
        {
            var tracks=new List<Track>();double duration=0;
            foreach(var channel in animation.GetProperty("channels").EnumerateArray())
            {
                var target=channel.GetProperty("target");var bone=System.Array.IndexOf(joints,target.GetProperty("node").GetInt32());if(bone<0)continue;
                var sampler=animation.GetProperty("samplers")[channel.GetProperty("sampler").GetInt32()];if(CrewCatalog.Text(sampler,"interpolation","LINEAR")!="LINEAR")throw new InvalidOperationException("Unsupported crew interpolation.");
                var times=Accessor(sampler.GetProperty("input").GetInt32());var values=Accessor(sampler.GetProperty("output").GetInt32());var path=CrewCatalog.Text(target,"path");var width=path=="rotation"?4:3;
                if(values.Length!=times.Length*width||times.Length==0||times.Zip(times.Skip(1)).Any(p=>p.First>=p.Second))throw new InvalidOperationException("Incomplete crew animation track.");
                duration=Math.Max(duration,times[^1]);tracks.Add(new(bone,path,times,values,width));
            }
            var name=CrewCatalog.Text(animation,"name");if(tracks.Count!=96)throw new InvalidOperationException("Missing crew bone channels.");clips.Add(name,new(name,duration,tracks));
        }
        if(Count!=244)throw new InvalidOperationException("Incomplete crew animation release.");
    }
    public bool Has(string clip)=>clips.ContainsKey(clip);
    public double Duration(string clip)=>clips.TryGetValue(clip,out var c)?c.Duration:0;
    public CrewBonePose[] Sample(string name,double time,bool loop)
    {
        var result=(CrewBonePose[])Rest.Clone();if(!clips.TryGetValue(name,out var clip))return result;
        var at=(float)(loop&&clip.Duration>0?Math.Max(0,time)%clip.Duration:Math.Clamp(time,0,clip.Duration));
        foreach(var track in clip.Tracks)
        {
            var i=System.Array.BinarySearch(track.Times,at);if(i<0)i=~i-1;i=Math.Clamp(i,0,track.Times.Length-1);var next=Math.Min(i+1,track.Times.Length-1);
            var fraction=next==i?0:Math.Clamp((at-track.Times[i])/(track.Times[next]-track.Times[i]),0,1);var a=i*track.Stride;var b=next*track.Stride;var old=result[track.Bone];
            Vector3 V(int o)=>new(track.Values[o],track.Values[o+1],track.Values[o+2]);Quaternion Q(int o)=>new(track.Values[o],track.Values[o+1],track.Values[o+2],track.Values[o+3]);
            result[track.Bone]=track.Path switch{"translation"=>old with{Position=Vector3.Lerp(V(a),V(b),fraction)},"scale"=>old with{Scale=Vector3.Lerp(V(a),V(b),fraction)},"rotation"=>old with{Rotation=Quaternion.Normalize(Quaternion.Slerp(Q(a),Q(b),fraction))},_=>old};
        }
        return result;
    }
}
