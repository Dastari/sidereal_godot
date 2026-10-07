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
    private static readonly Dictionary<string,string> weaponDetails = new();
    public static string Tooltip(ClientCore core,InventoryItemView item)
    {
        var basis=ItemDrag.Tooltip(item);var connection=core.Connection;if(connection==null)return basis;
        var pin=connection.Db.OwnItemDefinitionPins.Iter().FirstOrDefault(p=>p.ItemId==item.Id&&p.DefinitionId==item.DefinitionId);
        var revision=pin?.WeaponRevision??1;
        var row=connection.Db.PublishedItemDefinitions.Iter().FirstOrDefault(d=>d.Kind=="weapon"&&d.DefinitionId==item.DefinitionId&&d.Revision==revision&&d.Status is "published" or "retired");
        if(row==null)return basis;
        var key=row.DefinitionRef+":"+row.Sha256;
        if(weaponDetails.Count>4096)weaponDetails.Clear();
        if(!weaponDetails.TryGetValue(key,out var details))
        {
            try
            {
                using var document=JsonDocument.Parse(row.PayloadJson);var data=document.RootElement;
                double Number(string name){var number=data.GetProperty(name).GetDouble();if(!double.IsFinite(number)||number<0)throw new InvalidOperationException();return number;}
                string Format(double value)=>value.ToString("0.##",CultureInfo.InvariantCulture);
                var mode=data.TryGetProperty("mode",out var field)?field.GetString():"beam";var pellets=mode=="pellets"?Number("pellets"):1;
                var cooldown=Number("cooldownMs");var capacity=Number("capacity");if(cooldown<=0||capacity<=0)throw new InvalidOperationException();
                details=$"\nWeapon definition r{revision}\n{(mode=="thrown"?"Blast damage":"Damage")}: "+(pellets>1?$"{Format(pellets)} × ":"")+Format(Number("damage"))+
                    $"\nFire rate: {Format(1000/cooldown)} / s\n{(mode=="melee"?"Reach":mode=="thrown"?"Throw range":"Range")}: {Format(Number("rangeMeters"))} m\nEnergy cost: {Format(Number("shotCost"))} / {Format(capacity)}";
                foreach(var (name,label,unit,factor) in new[]{("reloadMs","Reload","s",1000d),("stunMs","Stun","s",1000d),("blastRadiusM","Blast radius","m",1d)})
                    if(data.TryGetProperty(name,out _))details+=$"\n{label}: {Format(Number(name)/factor)} {unit}";
            }
            catch(Exception error) when(error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException){details="\nWeapon definition unavailable.";}
            weaponDetails[key]=details;
        }
        return basis+details;
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
