#!/usr/bin/env python3
"""Copy immutable accepted browser combat FX and execute its original golden fixtures."""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess
import tempfile

SOURCE = "a35632deedf210cf43f64c43cb741d841f4a1ce0"
ROOT = pathlib.Path(__file__).resolve().parents[1]
DEST = ROOT / "Assets/Combat"

FIXTURE = r'''
import fs from "node:fs";
import path from "node:path";
import {pathToFileURL} from "node:url";
const source=process.argv[2],output=process.argv[3];
const mod=async(p:string)=>import(pathToFileURL(path.join(source,p)).href);
const content=await mod("packages/content/src/crew-items.ts");
const {createCombatFx}=await mod("packages/render/src/combat-fx.ts");
const {createImpactFlash}=await mod("packages/render/src/prefab-ship-interaction.ts");
const {worldToShip}=await mod("packages/sim/src/eva.ts");
const {createVoxelFxPlayer,crewItemFxTint,forwardRotation}=await mod("packages/render/src/equipment/voxel-item-fx.ts");
const {NullEngine}=await import("@babylonjs/core/Engines/nullEngine");
const {Scene}=await import("@babylonjs/core/scene");
const {TransformNode}=await import("@babylonjs/core/Meshes/transformNode");
const {Vector3}=await import("@babylonjs/core/Maths/math.vector");
const {SceneLoader}=await import("@babylonjs/core/Loading/sceneLoader");
const catalog=content.CREW_ITEM_CATALOG;
const publicBase=path.join(source,"apps/client/public",catalog.assetBase);
// This is the same byte adapter as the browser's existing NullEngine FX test: only
// GLB transport is replaced; the actual source player, sampler and callbacks execute.
const original=SceneLoader.LoadAssetContainerAsync.bind(SceneLoader);
SceneLoader.LoadAssetContainerAsync=(_base:any,file:any,scene:any)=>original("",
 "data:model/gltf-binary;base64,"+fs.readFileSync(path.join(publicBase,String(file))).toString("base64"),scene,undefined,".glb");
const metadata={fx:catalog.fx,items:catalog.items.map((item:any)=>({id:item.id,fx:item.fx,
 materials:content.crewItemMaterials(item),file:item.files.lod0,animationSet:item.animationSet})),
 tintGoldens:catalog.items.flatMap((item:any)=>catalog.fx.map((fx:any)=>({item:item.id,fx:fx.id,
  tint:crewItemFxTint(item,fx)?.asArray()??null}))),
 samples:catalog.fx.flatMap((fx:any)=>[-fx.durationS,0,fx.durationS*.1,fx.durationS*.25,
  fx.durationS*.5,fx.durationS*.8,fx.durationS,fx.durationS*1.2,fx.durationS*2.3].map(t=>({fx:fx.id,t,...content.sampleCrewItemFx(fx,t)})))};
const engine=new NullEngine();let dt=16;
engine.getDeltaTime=()=>dt;
const scene=new Scene(engine);scene.useRightHandedSystem=true;
const ship=new TransformNode("fixture-ship",scene);
const timeline:any[]=[];
for(const fx of catalog.fx){
 const player=createVoxelFxPlayer(scene,ship);
 const from=new Vector3(1,1.3,-2),to=new Vector3(1,1.3,-8);
 const travel=fx.kind==="projectile",holdS=fx.loop?fx.durationS*.8:undefined;
 await player.spawn(fx.id,{at:from,travelTo:travel?to:undefined,
  direction:travel?undefined:new Vector3(0,0,-1),lengthM:fx.lengthM?6:undefined,holdS,scale:.6});
 const root=scene.transformNodes.find((n:any)=>n.name===`crew-fx:${fx.id}`)!;
 const materials=[...new Set(root.getChildMeshes(false).map((m:any)=>m.material).filter(Boolean))] as any[];
 const base=materials.map(m=>({alpha:m.alpha,intensity:m.emissiveIntensity}));
 const frames:any[]=[];
 for(const deltaMs of [250,16,50,250,100,100,100,100,100,100,100,100,100,100,100,100]){
  dt=deltaMs;scene.onBeforeRenderObservable.notifyObservers(scene);
  const live=player.count>0;
  frames.push({delta:deltaMs/1000,live,position:live?root.position.asArray():null,
   scale:live?root.scaling.asArray():null,
   opacity:live?materials.map((m,j)=>base[j].alpha?m.alpha/base[j].alpha:0):null,
   emissive:live?materials.map((m,j)=>base[j].intensity?m.emissiveIntensity/base[j].intensity:0):null});
  if(!live)break;
 }
 timeline.push({fx:fx.id,from:from.asArray(),to:travel?to.asArray():null,lengthM:fx.lengthM?6:null,holdS:holdS??null,size:.6,frames});
 player.dispose();
}
const calls:any[]=[];
const body=new TransformNode("fixture-body",scene);body.position.y=.1875;
const fx={shot:(item:any,muzzle:any,rays:any)=>calls.push({kind:"shot",item:item.id,origin:muzzle.position.asArray(),direction:muzzle.direction.asArray(),rays:rays.map((r:any)=>({end:r.end.asArray(),struck:r.struck}))}),
 melee:(item:any,from:any,at:any)=>calls.push({kind:"melee",item:item.id,from:from.asArray(),at:at.asArray()}),
 blast:(item:any,at:any,radius:any)=>calls.push({kind:"blast",item:item.id,at:at.asArray(),radius}),
 stun:(at:any)=>calls.push({kind:"stun",at:at.asArray()})};
const combat=createCombatFx(scene,ship,fx,()=>({root:body}));
const action=(over:any={})=>({characterId:"actor",crewItemId:"pistol",mode:"beam",shotSequence:4n,
 points:[],originX:2,originY:-3,landX:0,landY:0,detonated:false,blastRadiusM:0,reloadSequence:0n,stunSequence:0n,...over});
combat.sync([action()]);
combat.sync([action({shotSequence:5n,points:[[4,3,1],[2,5,0]]})]);
combat.sync([action({shotSequence:5n,points:[[4,3,1]]})]);
combat.sync([action({shotSequence:5n,stunSequence:1n})]);
combat.sync([action({crewItemId:"baton",mode:"melee",shotSequence:6n,points:[[3,2,0]]})]);
combat.sync([action({crewItemId:"baton",mode:"melee",shotSequence:7n,points:[[3,2,1]]})]);
// Baseline a second actor's thrown event so the original detonation branch can run
// without requesting a thrown mesh; this is accepted history priming, not a shot.
combat.sync([action({characterId:"thrower",crewItemId:"grenade",mode:"thrown",shotSequence:1n,
 landX:3,landY:4,blastRadiusM:3.5})]);
combat.sync([action({characterId:"thrower",crewItemId:"grenade",mode:"thrown",shotSequence:1n,
 landX:3,landY:4,blastRadiusM:3.5,detonated:true})]);
combat.dispose();
let impactTime=0;
const flash=createImpactFlash(scene,ship,()=>impactTime);
const burst=flash.play(2,-3,1.3);
const legacy=[];
for(const delta of [0,.05,.25,.049,.001]){
 impactTime+=delta*1000;scene.onBeforeRenderObservable.notifyObservers(scene);
 legacy.push({delta,age:impactTime/1000,live:flash.meshes().length>0,
  coreScale:flash.meshes().length?burst[0].scaling.x:null,
  opacity:flash.meshes().length?burst[0].visibility:null,
  positions:flash.meshes().map((m:any)=>m.position.asArray())});
}
flash.dispose();
const worldProjection=[0,.55,Math.PI/2,-1.22,Math.PI].map(heading=>{
 const ship={x:1e12,y:-1e12,heading},world=[ship.x+.125,ship.y-.25] as const;
 const local=worldToShip(ship,world);
 return {ship,world,height:1.3,expected:[local[0],1.3,-local[1]]};
});
const rotations=[[0,0,-1],[0,0,1],[0,1,0],[0,-1,0],[1,0,0],[-1,0,0],[1,2,-3]].map(direction=>
 ({direction,quaternion:forwardRotation(Vector3.FromArray(direction)).asArray()}));
scene.dispose();engine.dispose();
fs.writeFileSync(output,JSON.stringify({metadata,timeline,calls,legacy,worldProjection,rotations}));
'''


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(command, cwd):
    return subprocess.run(command, cwd=cwd, check=True, text=True, capture_output=True, timeout=120)


def generate(source):
    if run(["git", "rev-parse", "HEAD"], source).stdout.strip() != SOURCE:
        raise SystemExit("Browser source revision differs from the immutable combat pin")
    governed = ["packages/content", "packages/render", "packages/sim", "apps/client/src/App.tsx",
                "apps/client/src/crewmates.ts", "package-lock.json"]
    if run(["git", "status", "--porcelain", "--", *governed], source).stdout.strip():
        raise SystemExit("Combat source or dependency lock has uncommitted modifications")
    DEST.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="sidereal-combat-", dir=source) as scratch:
        helper = pathlib.Path(scratch) / "fixture.mts"
        result = pathlib.Path(scratch) / "fixture.json"
        helper.write_text(FIXTURE)
        run([str(source / "node_modules/.bin/tsx"), "--tsconfig", str(source / "tsconfig.json"),
             str(helper), str(source), str(result)], source)
        fixture = json.loads(result.read_text())
    metadata = fixture.pop("metadata")
    public = source / "apps/client/public/assets/crew/items/r001"
    assets = []
    files = [(row["file"], row["file"]) for row in metadata["fx"]]
    # The actual browser thrown visual uses the original r001 catalogue mesh,
    # independently of the worn study body. Keep those two small public meshes exact.
    files += [(row["file"], "items/" + row["file"]) for row in metadata["items"] if row["animationSet"] == "throw"]
    for original, relative in files:
        asset = public / original
        target = DEST / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(asset, target)
        assets.append({"file": relative, "sourceFile": "apps/client/public/assets/crew/items/r001/" + original,
                       "sha256": digest(asset), "bytes": asset.stat().st_size})
    for row in metadata["items"]:
        if row["animationSet"] == "throw": row["thrownFile"] = "items/" + row["file"]
    goldens = {"sourceCommit": SOURCE, "sampler": metadata.pop("samples"), "tints": metadata.pop("tintGoldens"), **fixture}
    golden_file = DEST / "golden.json"
    golden_file.write_text(json.dumps(goldens, separators=(",", ":")) + "\n")
    source_files = ["packages/content/src/crew-items.ts", "packages/content/src/crew-items-r001.json",
                    "packages/content/src/crew-items-r001-armed.json", "packages/render/src/combat-fx.ts",
                    "packages/render/src/equipment/voxel-item-fx.ts", "packages/render/src/equipment/voxel-items.ts", "packages/render/src/crew/remote-crew.ts",
                    "packages/render/src/prefab-ship-interaction.ts", "packages/render/src/index.ts", "packages/sim/src/eva.ts",
                    "apps/client/src/App.tsx", "apps/client/src/crewmates.ts", "package-lock.json"]
    manifest = {"schema": "sidereal.native-combat-fx.v1", "sourceCommit": SOURCE, "catalogRevision": "r001",
                "sourceHashes": {p: digest(source / p) for p in source_files}, "assets": assets,
                "golden": {"file": "golden.json", "sha256": digest(golden_file), "bytes": golden_file.stat().st_size}, **metadata}
    (DEST / "manifest.json").write_text(json.dumps(manifest, separators=(",", ":")) + "\n")


def verify():
    manifest = json.loads((DEST / "manifest.json").read_text())
    if manifest["schema"] != "sidereal.native-combat-fx.v1" or manifest["sourceCommit"] != SOURCE:
        raise SystemExit("Unsupported combat source manifest")
    for row in [*manifest["assets"], manifest["golden"]]:
        path = (DEST / row["file"]).resolve()
        if not path.is_relative_to(DEST.resolve()) or path.stat().st_size != row["bytes"] or digest(path) != row["sha256"]:
            raise SystemExit("Combat asset bytes/hash differ: " + row["file"])
    if len(manifest["fx"]) != 15 or len(manifest["assets"]) != 17:
        raise SystemExit("Incomplete accepted combat library")
    print(f"Combat source {SOURCE}: 15 FX, 2 exact thrown meshes, {len(manifest['items'])} item mappings; "
          f"{sum(row['bytes'] for row in manifest['assets'])} unchanged GLB bytes and independent source goldens verified.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=pathlib.Path, default=pathlib.Path("/root/sidereal-worktrees/shadow-transform-refresh"))
    parser.add_argument("--verify", action="store_true")
    args = parser.parse_args()
    if not args.verify: generate(args.source.resolve())
    verify()
