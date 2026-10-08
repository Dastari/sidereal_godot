using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sidereal.Bindings;
namespace Sidereal.Ui;

public sealed record ComponentPresentation(string Name,double MaximumHp);
public sealed record ConfiguredIntegrity(double Hp,double MaximumHp,int Damaged,int Count);
// Browser ship-components.v1.json catalog r4, SHA256 b85f18272abb9fbcefbb3c4a0214c65fdd4f71df7633161598dfe6f4e7745d8b.
// Presentation only. Revisions outside this exact catalog return unavailable; no installation or hull HP is implied.
public static class ConfiguredComponentCatalog
{
    public static readonly IReadOnlyDictionary<string,ComponentPresentation> RevisionFour = new Dictionary<string,ComponentPresentation>
    {
        ["ion-drive.sm"] = new("Ion drive SM", 150),
        ["ion-drive.md"] = new("Ion drive MD", 300),
        ["ion-drive.lg"] = new("Ion drive LG", 520),
        ["ion-drive.xl"] = new("Ion drive XL", 820),
        ["thrust-block.sm"] = new("Thrust block SM", 160),
        ["thrust-block.md"] = new("Thrust block MD", 340),
        ["thrust-block.lg"] = new("Thrust block LG", 600),
        ["thrust-block.xl"] = new("Thrust block XL", 950),
        ["ion-drive.salvaged.sm"] = new("Salvaged ion drive SM", 105),
        ["ion-drive.salvaged.md"] = new("Salvaged ion drive MD", 210),
        ["ion-drive.salvaged.lg"] = new("Salvaged ion drive LG", 364),
        ["ion-drive.salvaged.xl"] = new("Salvaged ion drive XL", 574),
        ["resonance-drive.aurelian.sm"] = new("Aurelian resonance drive SM", 120),
        ["resonance-drive.aurelian.md"] = new("Aurelian resonance drive MD", 260),
        ["resonance-drive.aurelian.lg"] = new("Aurelian resonance drive LG", 460),
        ["rcs.sm"] = new("RCS thruster SM", 60),
        ["rcs.md"] = new("RCS thruster MD", 120),
        ["vtol-thruster.sm"] = new("VTOL thruster SM", 120),
        ["vtol-thruster.md"] = new("VTOL thruster MD", 260),
        ["warp-drive.lg"] = new("Warp drive LG", 600),
        ["warp-drive.xl"] = new("Warp drive XL", 1000),
        ["reactor.sm"] = new("Fusion reactor SM", 300),
        ["reactor.md"] = new("Fusion reactor MD", 650),
        ["reactor.lg"] = new("Fusion reactor LG", 1100),
        ["battery.sm"] = new("Battery bank SM", 120),
        ["battery.md"] = new("Battery bank MD", 240),
        ["battery.lg"] = new("Battery bank LG", 420),
        ["capacitor.sm"] = new("Capacitor bank SM", 80),
        ["capacitor.md"] = new("Capacitor bank MD", 160),
        ["capacitor.lg"] = new("Capacitor bank LG", 280),
        ["fuel-tank.sm"] = new("Fuel tank SM", 100),
        ["fuel-tank.md"] = new("Fuel tank MD", 220),
        ["fuel-tank.lg"] = new("Fuel tank LG", 420),
        ["solar-array.sm"] = new("Solar array SM", 40),
        ["solar-array.md"] = new("Solar array MD", 80),
        ["aux-generator.sm"] = new("Fuel-cell generator SM", 120),
        ["radiator.sm"] = new("Radiator panel SM", 60),
        ["radiator.md"] = new("Radiator panel MD", 120),
        ["radiator.lg"] = new("Radiator panel LG", 200),
        ["coolant-pump.sm"] = new("Coolant pump loop SM", 80),
        ["coolant-pump.md"] = new("Coolant pump loop MD", 150),
        ["coolant-pump.lg"] = new("Coolant pump loop LG", 260),
        ["heat-sink.sm"] = new("Heat sink SM", 120),
        ["heat-sink.md"] = new("Heat sink MD", 240),
        ["point-defense.sm"] = new("Point-defense turret SM", 90),
        ["point-defense.md"] = new("Point-defense turret MD", 180),
        ["autocannon.sm"] = new("Twin autocannon SM", 120),
        ["autocannon.md"] = new("Twin autocannon MD", 260),
        ["autocannon.lg"] = new("Twin autocannon LG", 460),
        ["laser-cannon.sm"] = new("Laser cannon SM", 110),
        ["laser-cannon.md"] = new("Laser cannon MD", 240),
        ["laser-cannon.lg"] = new("Laser cannon LG", 420),
        ["railgun.md"] = new("Railgun mount MD", 300),
        ["railgun.lg"] = new("Railgun mount LG", 520),
        ["missile-pod.sm"] = new("Missile pod SM", 110),
        ["missile-pod.md"] = new("Missile pod MD", 230),
        ["missile-pod.lg"] = new("Missile pod LG", 400),
        ["torpedo-launcher.md"] = new("Torpedo launcher MD", 320),
        ["torpedo-launcher.lg"] = new("Torpedo launcher LG", 560),
        ["flak-cannon.sm"] = new("Flak cannon SM", 110),
        ["flak-cannon.md"] = new("Flak cannon MD", 230),
        ["flak-cannon.lg"] = new("Flak cannon LG", 400),
        ["plasma-turret.md"] = new("Plasma turret MD", 240),
        ["plasma-turret.lg"] = new("Plasma turret LG", 420),
        ["side-cannon.sm"] = new("Side cannon sponson SM", 120),
        ["side-cannon.md"] = new("Side cannon sponson MD", 250),
        ["side-cannon.lg"] = new("Side cannon sponson LG", 440),
        ["magazine.ballistic.sm"] = new("Ballistic magazine SM", 150),
        ["magazine.ballistic.md"] = new("Ballistic magazine MD", 300),
        ["magazine.ballistic.lg"] = new("Ballistic magazine LG", 520),
        ["magazine.missile.md"] = new("Missile magazine MD", 260),
        ["magazine.missile.lg"] = new("Missile magazine LG", 480),
        ["magazine.torpedo.lg"] = new("Torpedo rack LG", 560),
        ["shield-generator.sm"] = new("Shield generator SM", 160),
        ["shield-generator.md"] = new("Shield generator MD", 340),
        ["shield-generator.lg"] = new("Shield generator LG", 600),
        ["shield-emitter.sm"] = new("Shield emitter SM", 50),
        ["shield-emitter.md"] = new("Shield emitter MD", 110),
        ["shield-emitter.lg"] = new("Shield emitter LG", 200),
        ["armor-plate.light.sm"] = new("Light armour plate SM", 120),
        ["armor-plate.medium.sm"] = new("Medium armour plate SM", 260),
        ["armor-plate.heavy.sm"] = new("Heavy armour plate SM", 480),
        ["armor-plate.reactive.sm"] = new("Reactive armour plate SM", 320),
        ["sensor-dish.sm"] = new("Sensor dish SM", 60),
        ["sensor-dish.md"] = new("Sensor dish MD", 120),
        ["sensor-dish.lg"] = new("Sensor dish LG", 220),
        ["radar-array.md"] = new("Radar array MD", 140),
        ["radar-array.lg"] = new("Radar array LG", 260),
        ["scanner-mast.sm"] = new("Scanner mast SM", 50),
        ["scanner-mast.md"] = new("Scanner mast MD", 100),
        ["relay-beacon.sm"] = new("Relay beacon SM", 40),
        ["relay-beacon.md"] = new("Relay beacon MD", 90),
        ["tractor-projector.sm"] = new("Tractor projector SM", 90),
        ["tractor-projector.md"] = new("Tractor projector MD", 190),
        ["tractor-projector.lg"] = new("Tractor projector LG", 340),
        ["salvage-arm.md"] = new("Salvage arm MD", 200),
        ["salvage-arm.lg"] = new("Salvage arm LG", 360),
        ["docking-clamp.md"] = new("Docking clamp MD", 240),
        ["docking-clamp.lg"] = new("Docking clamp LG", 420),
        ["mining-laser.sm"] = new("Mining laser SM", 100),
        ["mining-laser.md"] = new("Mining laser MD", 210),
        ["mining-laser.lg"] = new("Mining laser LG", 380),
        ["drone-bay.md"] = new("Drone bay MD", 260),
        ["drone-bay.lg"] = new("Drone bay LG", 460),
        ["cargo-door.2m"] = new("Cargo bay door SM", 200),
        ["cargo-door.4m"] = new("Cargo bay door MD", 400),
        ["cargo-door.6m"] = new("Cargo bay door LG", 600),
        ["airlock.exterior.md"] = new("Exterior airlock MD", 300),
        ["airlock.interior.sm"] = new("Interior airlock door SM", 200),
        ["hatch.sm"] = new("Deck hatch SM", 150),
        ["hatch.exterior.sm"] = new("Exterior roof hatch SM", 220),
        ["docking-port.md"] = new("Docking port MD", 320),
        ["docking-port.lg"] = new("Docking port LG", 540),
        ["life-support.sm"] = new("Life support unit SM", 120),
        ["life-support.md"] = new("Life support unit MD", 240),
        ["life-support.lg"] = new("Life support unit LG", 420),
        ["air-filter.sm"] = new("Air filter SM", 60),
        ["air-filter.md"] = new("Air filter MD", 120),
        ["oxygen-tank.sm"] = new("Oxygen tank SM", 80),
        ["oxygen-tank.md"] = new("Oxygen tank MD", 160),
        ["hydroponics.sm"] = new("Hydroponics rack SM", 40),
        ["gravity-unit.md"] = new("Gravity unit MD", 200),
        ["gravity-unit.lg"] = new("Gravity unit LG", 360),
        ["computer-core.sm"] = new("Computer core SM", 80),
        ["computer-core.md"] = new("Computer core MD", 160),
        ["computer-core.lg"] = new("Computer core LG", 280),
        ["console.navigation.sm"] = new("Navigation console SM", 60),
        ["console.command.sm"] = new("Command console SM", 60),
        ["console.fire-control.sm"] = new("Fire-control console SM", 60),
        ["console.engineering.sm"] = new("Engineering console SM", 60),
        ["console.sensor.sm"] = new("Sensor console SM", 60),
        ["crew-bunk.sm"] = new("Crew bunk SM", 60),
    };
    public static ConfiguredIntegrity? Read(string shipId,bool owned,string documentJson,IEnumerable<ShipComponentDamageStatus> damage)
    {
        if(!owned)return null;
        try
        {
            using var document=JsonDocument.Parse(documentJson);var binding=document.RootElement.GetProperty("prefab");
            if(binding.GetProperty("catalog").GetString()!="ship-components-v1@4")return null;
            var mounts=binding.GetProperty("document").GetProperty("mounts");
            var rows=damage.Where(d=>d.ShipId==shipId).ToDictionary(d=>d.ObjectId);double hp=0,max=0;var damaged=0;
            foreach(var mount in mounts.EnumerateArray())
            {
                var component=mount.GetProperty("component").GetString()!;if(!RevisionFour.TryGetValue(component,out var definition))return null;
                rows.TryGetValue("mount:"+mount.GetProperty("id").GetString(),out var row);if(row!=null&&row.ComponentId!=component)return null;
                var maximum=row?.MaxHp??definition.MaximumHp;var current=row?.Hp??maximum;
                if(!double.IsFinite(maximum)||!double.IsFinite(current)||maximum<=0||current<0||current>maximum)return null;
                hp+=current;max+=maximum;if(current<maximum)damaged++;
            }
            return max>0?new(hp,max,damaged,mounts.GetArrayLength()):null;
        }
        catch(Exception exception) when(exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException){return null;}
    }
}
