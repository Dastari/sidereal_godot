using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Globalization;
using Sidereal.Native;

namespace Sidereal.InventoryUi;

/// <summary>Shared browser presentation rarity; this grants no gameplay value.</summary>
internal static class ItemPresentation
{
    internal sealed record Stat(string Label,string Value,double Fraction);
    internal sealed record Details(string Category,string Description,Stat[] Stats,string? Status);
    private static readonly Dictionary<string,(Stat[] Stats,string? Status)> weaponDetails = new();
    internal static (string Key,string Payload)? ResolveWeaponPayload(string definitionId,ulong revision,IEnumerable<Sidereal.Bindings.PublishedItemDefinition> rows)
        => Sidereal.Native.Input.PinnedWeaponDefinitions.ResolvePayload(definitionId,revision,rows);
    private static readonly Dictionary<string,string> descriptions = new()
    {
        ["compact-pistol"]="A compact sidearm built for close quarters aboard crowded ships. A dependable companion on an uncertain deck.",
        ["heavy-handgun"]="A reinforced heavy sidearm with a substantial power chamber. Built to stop a threat before it closes the gap.",
        ["carbine"]="A versatile frontier carbine with a stabilized receiver and compact stock. Balanced for patrols and boarding operations.",
        ["long-rifle"]="A precision survey rifle with an extended barrel and optical sight. Designed for deliberate fire across open terrain.",
        ["scanner"]="A handheld survey instrument for inspecting unfamiliar materials and searching for useful signatures.",
        ["plasma-cutter"]="A rugged industrial cutter that concentrates energy at its working tip. A salvage crew's essential workshop tool.",
        ["medkit"]="A sealed field kit containing dressings, diagnostic patches and emergency medical supplies.",
        ["power-cell"]="A replaceable energy cell with protected contacts and a durable transport shell.",
        ["field-pack"]="A practical expedition backpack with reinforced compartments for tools and supplies. Contents travel with the pack.",
        ["resource-canister"]="A pressure-rated fuel vessel with a sealed cap. Its reservoir remains attached when the canister changes hands."
    };
    public static Details ReadDetails(ClientCore core,InventoryItemView item)
    {
        var definition=item.Definition;
        if(definition==null)return new("Item","",Array.Empty<Stat>(),"This pinned item definition is unavailable. Reconnect or update the client to load it.");
        var category=definition.Category switch {"weapon"=>"Weapon","medical"=>"Medical","utility"=>"Utility","tool"=>"Tool",_=>definition.EquipSlot.Length>0?"Equipment":"Item"};
        if(definition.Role.Length>0&&!definition.Role.Equals(category,StringComparison.OrdinalIgnoreCase))category+=" · "+definition.Role;
        var description=descriptions.GetValueOrDefault(definition.Id,"");
        if(definition.Category!="weapon")return new(category,description,Array.Empty<Stat>(),null);
        var pin=core.Connection?.Db.OwnItemDefinitionPins.Iter().FirstOrDefault(p=>p.ItemId==item.Id&&p.DefinitionId==item.DefinitionId);
        var revision=pin?.WeaponRevision??1;
        var payload=ResolveWeaponPayload(item.DefinitionId,revision,core.Connection?.Db.PublishedItemDefinitions.Iter()??Array.Empty<Sidereal.Bindings.PublishedItemDefinition>());
        if(payload==null)return new(category,description,Array.Empty<Stat>(),"Weapon statistics unavailable.");
        var key=payload.Value.Key;
        if(weaponDetails.Count>4096)weaponDetails.Clear();
        if(!weaponDetails.TryGetValue(key,out var detail))
        {
            try
            {
                using var document=JsonDocument.Parse(payload.Value.Payload);var data=document.RootElement;
                double Number(string name){var n=data.GetProperty(name).GetDouble();if(!double.IsFinite(n)||n<0)throw new InvalidOperationException();return n;}
                string Format(double value)=>value.ToString("0.##",CultureInfo.InvariantCulture);
                Stat Value(string label,string value,double fraction){if(!double.IsFinite(fraction))throw new InvalidOperationException();return new(label,value,Math.Clamp(fraction,0,1));}
                var mode=data.TryGetProperty("mode",out var field)?field.GetString():"beam";var pellets=mode=="pellets"?Number("pellets"):1;
                var cooldown=Number("cooldownMs");var capacity=Number("capacity");if(cooldown<=0||capacity<=0)throw new InvalidOperationException();
                var damage=Number("damage");var rate=1000/Math.Max(1,cooldown);var range=Number("rangeMeters");var cost=Number("shotCost");
                var stats=new List<Stat>
                {
                    Value(mode=="thrown"?"Blast damage":"Damage",(mode=="pellets"?Format(pellets)+" × ":"")+Format(damage),damage*pellets/80),
                    Value("Fire rate",rate.ToString(rate>=10?"F0":"F1",CultureInfo.InvariantCulture)+" / s",rate/16),
                    Value(mode=="melee"?"Reach":mode=="thrown"?"Throw range":"Range",Format(range)+" m",range/90),
                    Value("Energy cost",Format(cost)+" / "+Format(capacity),cost/capacity)
                };
                foreach(var (name,label,denominator) in new[]{("reloadMs","Reload",3500d),("stunMs","Stun",3000d)})
                    if(data.TryGetProperty(name,out _)){var n=Number(name);if(n>0)stats.Add(Value(label,(n/1000).ToString("F1",CultureInfo.InvariantCulture)+" s",n/denominator));}
                if(data.TryGetProperty("blastRadiusM",out _)){var n=Number("blastRadiusM");if(n>0)stats.Add(Value("Blast radius",Format(n)+" m",n/5));}
                detail=(stats.ToArray(),null);
            }
            catch(Exception error) when(error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException){detail=(Array.Empty<Stat>(),"Weapon statistics unavailable.");}
            weaponDetails[key]=detail;
        }
        return new(category,description,detail.Stats,detail.Status);
    }
    public static string Tooltip(ClientCore core,InventoryItemView item)
    {
        var details=ReadDetails(core,item);return ItemDrag.Tooltip(item)+
            (details.Stats.Length>0?"\n"+string.Join("\n",details.Stats.Select(s=>s.Label+": "+s.Value)):"")+
            (details.Status!=null?"\n"+details.Status:"");
    }
    public static string Rarity(ItemDefinition? definition)
    {
        if (definition == null) return "common";
        if (definition.Rarity is "uncommon" or "rare" or "epic" or "legendary") return definition.Rarity;
        var armor = definition.Id.StartsWith("crew-") ? definition.Id.Split('-')[1] : "";
        return definition.Id switch {
            "heavy-handgun" or "scanner" or "power-cell" => "rare", "carbine" => "epic",
            "long-rifle" or "plasma-cutter" => "legendary", "field-pack" => "uncommon",
            _ => armor switch { "captain" => "legendary", "marine" or "scientist" => "epic",
                "security" or "medic" or "recon" or "pilot" => "rare", "engineer" or "salvage" => "uncommon", _ => "common" }
        };
    }
    public static string Category(InventoryItemView item, InventorySnapshot snapshot)
        => snapshot.Containers.Any(c=>c.ParentItemId==item.Id) ? "Storage" :
            item.Definition?.EquipSlot is { Length:>0 } slot && slot is not ("hand" or "back") ? "Armor" :
            item.Definition?.Category switch { "weapon"=>"Weapons", "medical"=>"Supplies", "tool" or "utility"=>"Tools", _=>"Supplies" };
}
