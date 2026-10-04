#!/usr/bin/env python3
"""Bundle immutable, published browser ship assets and canonical placement data.

Normal clone/build needs no browser repository. --verify checks bundled bytes.
Maintainers regenerate from a read-only browser checkout with Node dependencies:
  python3 scripts/godot_world_assets.py --source-repo /root/sidereal_spacetime
No server access, database publication, geometry generation, or art reauthoring occurs.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import pathlib
import subprocess
import struct
import tarfile
import tempfile

SOURCE_REVISION = "e749d5d877ef18ac7421fb003f55effd7e01a404"
ROOT = pathlib.Path(__file__).resolve().parents[1]
DEST = ROOT / "Assets" / "World"

EXPORT = r'''
import fs from 'node:fs';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {PREFAB_SHIPS} from './packages/content/src/prefabs/index.ts';
import {defaultPrefabComponentCatalog} from './packages/content/src/ship-prefab-catalog.ts';
import {canonicalShipPrefabJson,prefabOrigin,placeMount,volumeGeometry} from './packages/content/src/ship-prefab.ts';
import {prefabConstructionDocument} from './packages/sim/src/prefab-construction.ts';
import {dressShip} from './packages/sim/src/ship-dresser.ts';
import {compileAuthoredTemplatePlan} from './packages/sim/src/authored-template-plan.ts';
import {AUTHORED_TEMPLATE_KIT_BASE,AUTHORED_TEMPLATE_KIT_MANIFEST_SHA256,readAuthoredTemplateKit} from './packages/content/src/authored-template-kit.ts';
import {TEMPLATE_OBJECT_PIECES,authoredInteriorComponentPiece,authoredTemplateObjects,authoredTemplateComponents} from './packages/render/src/prefab-ship/authored-template-objects.ts';
import {authoredTemplatePropulsion} from './packages/render/src/prefab-ship/authored-template-propulsion.ts';
import {authoredInstanceMatrix} from './packages/render/src/prefab-ship/wayfarer-authored-study.ts';
import {authoredProfilePoint,prefabClipPlanes,clipAuthoredGeometry} from './packages/render/src/prefab-ship/authored-template-geometry.ts';
import {readAuthoredAssetLighting} from './packages/render/src/authored-asset-lighting.ts';
import {Matrix,Vector3} from '@babylonjs/core/Maths/math.vector';
import {SHIP_THEMES} from './packages/content/src/ship-themes.ts';
import {deckObjectVisualUrl,interiorArtQuarterTurns} from './packages/content/src/ship-furniture.ts';
import {readWayfarerAuthoredStudy} from './packages/content/src/wayfarer-authored-study.ts';
import {readWayfarerAuthoredFlight} from './packages/content/src/wayfarer-authored-flight.ts';
import {applyWayfarerAuthoredPlacementEdits,WAYFARER_GAMEPLAY_OBJECTS} from './packages/content/src/wayfarer-authored-gameplay.ts';
import {WAYFARER_MOVABLE_FURNISHINGS} from './packages/content/src/wayfarer-furnishings.ts';
import {WAYFARER_POST_APERTURES,wayfarerNearWallPlacements} from './packages/render/src/prefab-ship/wayfarer-authored-details.ts';
import {kitInstanceMatrix,componentMatrix,GLTF_TO_ZUP,multiply,prefabFrameMatrix,mountRotation,frameOfSocket} from './packages/render/src/prefab-ship/frames.ts';
const read=(p:string)=>JSON.parse(fs.readFileSync(p,'utf8'));
const sha=(s:string|Buffer)=>createHash('sha256').update(s).digest('hex');
const catalog=defaultPrefabComponentCatalog();
const assets=new Map<string,any>();
function asset(source:string,pin?:string,node?:string) {
  source=source.replace(/^\/assets\//,'assets/runtime/');
  const bytes=fs.readFileSync(source), digest=sha(bytes);
  if(pin && pin!==digest) throw Error('Changed source asset '+source);
  const file=source.replace(/^assets\/runtime\//,'');
  assets.set(file,{file,source,sha256:digest,bytes:bytes.length});
  return {file,node:node??null};
}
function finalMatrix(m:number[],origin:number[],height=0) {
 const v=multiply(m,prefabFrameMatrix(origin as [number,number])); v[13]+=height; return v;
}
const kit=read('assets/runtime/ship-kit/r002/manifest.json');
const authoredBase='assets/runtime/ship-study/wayfarer-authored-r001/';
const flightBase='assets/runtime/ship-study/wayfarer-dorsal-r001/';
const study=readWayfarerAuthoredStudy(read(authoredBase+'manifest.json'),read(authoredBase+'layout.json'),read(authoredBase+'descriptor.json'));
const flight=readWayfarerAuthoredFlight(read(flightBase+'descriptor.json'));
const templateManifest=AUTHORED_TEMPLATE_KIT_BASE.replace(/^\/assets\//,'assets/runtime/')+'manifest.json';
if(sha(fs.readFileSync(templateManifest))!==AUTHORED_TEMPLATE_KIT_MANIFEST_SHA256)throw Error('Changed authored template manifest');
const templateKit=readAuthoredTemplateKit(read(templateManifest));
const propLightingPath='assets/runtime/ship-study/wayfarer-object-lighting-r002/descriptor.json';
if(sha(fs.readFileSync(propLightingPath))!=='58e7ab323edbadc488dcbc707e40a431249011bebf59ecfe205df1359540a017')throw Error('Changed authored template prop lighting');
const templateLighting=new Map(readAuthoredAssetLighting(read(propLightingPath)));
if(templateKit.lighting)for(const [pin,light] of readAuthoredAssetLighting(templateKit.lighting))templateLighting.set(pin,light);
function templateRows(doc:any,dressed:any,origin:number[]) {
 const plan=compileAuthoredTemplatePlan(doc,{catalog}),propMap=new Map(study.pieces.map(p=>[p.id,p]));
 const propulsion=authoredTemplatePropulsion(dressed,propMap,catalog);
 const source=new Map([...templateKit.pieces.map(p=>[p.id,{...p,base:AUTHORED_TEMPLATE_KIT_BASE}]),...study.pieces.map(p=>[p.id,{...p,base:'/assets/ship-study/wayfarer-authored-r001/'}]),...propulsion.pieces.map(p=>[p.id,p])]);
 const rows=[...plan.instances,...authoredTemplateObjects(dressed,propMap),...authoredTemplateComponents(dressed,propMap),...propulsion.instances];
 const lights:any[]=[];
 const placements=rows.map((row:any)=>{
  const piece:any=source.get(row.piece);if(!piece)throw Error('Missing native template piece '+row.piece);
  const matrix=authoredInstanceMatrix(piece.frame,row.matrix,origin as [number,number]);
  const clipping=prefabClipPlanes(row.clipPlanes??[],origin as [number,number]);
  const assetLight=templateLighting.get(piece.sha256);
  for(const socket of assetLight?.sockets??[]) {
   let point=Vector3.TransformCoordinates(Vector3.FromArray(Array.from(socket.position)),Matrix.FromArray(matrix));
   let direction=socket.direction?Vector3.TransformNormal(Vector3.FromArray(Array.from(socket.direction)),Matrix.FromArray(matrix)):null;
   if(row.verticalProfile){const warped=authoredProfilePoint(point,row.verticalProfile,origin as [number,number]);point=warped.position;if(direction)direction=Vector3.TransformNormal(direction,warped.jacobian);}
   if(clipping.every(p=>p[0]*point.x+p[1]*point.y+p[2]*point.z+p[3]>=-1e-8))lights.push({id:row.object+':'+socket.id,at:point.asArray(),colour:socket.color,energy:socket.intensity,range:socket.range,view:row.view,...(direction?{direction:direction.normalize().asArray(),angle:socket.angle}:{}),owner:row.object,sourceSha256:piece.sha256});
  }
  return {id:row.object,piece:row.piece,role:row.role,view:row.view,region:row.region,matrix,...asset(piece.base+piece.file,piece.sha256),authored:true,themeSlots:true,movable:false,clipPlanes:clipping,verticalProfile:row.verticalProfile??null};
 });
 return {placements,lights,plan,propulsion};
}
function authoredRows(rows:any[],pieces:any[],base:string,view:string,height:number) {
 const map=new Map(pieces.map(p=>[p.id,p]));
 return rows.map(row=>{
  const piece:any=map.get(row.piece); if(!piece)throw Error('Unknown piece '+row.piece);
  const converted=row.frame==='ship-node-baked'?GLTF_TO_ZUP:multiply(GLTF_TO_ZUP,Array.from({length:16},(_,i)=>row.matrix[i%4][Math.floor(i/4)]));
  const source=WAYFARER_GAMEPLAY_OBJECTS.find(s=>s.object===row.object);
  return {id:row.object,piece:row.piece,role:row.role,view,matrix:finalMatrix(converted,[0,0],height),
   ...asset(base+piece.file,piece.sha256),authored:true,
   movable:WAYFARER_MOVABLE_FURNISHINGS.has(row.object),
   pivot:source?[-(source.min[1]+source.max[1])/2,(source.min[2]+source.max[2])/2+height,-(source.min[0]+source.max[0])/2]:null};
 });
}
const ships=PREFAB_SHIPS.map(doc=>{
 const origin=prefabOrigin(doc), canonical=canonicalShipPrefabJson(doc);
 let placements:any[]=[],lights:any[]=[],native:any=null;
 if(doc.id==='fed.m.wayfarer') {
  const rows=study.instances.filter(r=>!['Hall_crew_chibi','Hall_selection_ring'].includes(r.object)).map(r=>({...r,matrix:applyWayfarerAuthoredPlacementEdits(r.object,r.matrix)}));
  rows.push(...wayfarerNearWallPlacements(study.pieces));
  const selected=study.pieces.map(p=>WAYFARER_POST_APERTURES[p.id]?{...p,...WAYFARER_POST_APERTURES[p.id]}:p);
  const normal=rows.filter(r=>!WAYFARER_POST_APERTURES[r.piece]);
  placements.push(...authoredRows(normal,selected,authoredBase,'deck',.1875));
  const posts=rows.filter(r=>WAYFARER_POST_APERTURES[r.piece]);
  placements.push(...authoredRows(posts,selected,'assets/runtime/ship-study/wayfarer-details-r001/','deck',.1875));
  placements.push(...authoredRows(flight.instances,flight.pieces,flightBase,'flight',.1875));
  const rcs={id:'engine.rcs.md.wayfarer-r001',file:'rcs.md.glb',sha256:'9e7f2d54f90e3d5f0cd0e1900b6d76f9534e33c9552abe21ce6dd7e7b50eda51'};
  for(const mount of doc.mounts.filter(m=>m.component.startsWith('rcs.'))) {
   const p=placeMount(mount,catalog.get(mount.component),doc.volumes.map(volumeGeometry),doc);
   const m=multiply(GLTF_TO_ZUP,componentMatrix(p.anchor,p.anchorZ/16-.1875,p.quarterTurns));
   placements.push({id:'RCS_'+mount.id,piece:rcs.id,role:'engine-pod',view:'both',matrix:finalMatrix(m,[0,0],.1875),...asset(authoredBase+rcs.file,rcs.sha256),authored:true,movable:false});
  }
  const selectedLights=new Set(['LT_pool_8','LT_pool_9','LT_pool_15','LT_pool_31','LT_pool_34','LT_pool_14','LT_pool_21','LT_pool_22']);
  lights=read(authoredBase+'layout.json').lights.filter((r:any)=>selectedLights.has(r.name)).map((r:any)=>({id:r.name,at:[-r.location[1],r.location[2]+.1875,-r.location[0]],colour:r.colour,energy:.7,range:3.5}));
 } else {
  const dressed=dressShip(doc,{catalog});
  native=templateRows(doc,dressed,origin);
  placements=[...native.placements,...dressed.kit.filter((p:any)=>native.plan.retainedLegacyPieces.includes(p.piece)).map((p:any,i:number)=>{
    const k=kit.pieces[p.piece]; if(!k)throw Error('Missing published kit '+p.piece);
    return {id:'kit:'+i,piece:p.piece,role:p.piece.startsWith('int.floor')?'floor':p.piece.startsWith('int.')?'wall':'hull',view:p.view,matrix:finalMatrix(kitInstanceMatrix(p.x,p.y,p.z,p.rotDeg,p.mirror),origin),...asset('assets/runtime/ship-kit/r002/'+k.file,k.sha256,k.node),authored:false,movable:false};
  })];
  for(const c of dressed.components) {
    if((c.placement.mount.attach==='interior'&&authoredInteriorComponentPiece(c.component))||native.propulsion.replacedMounts.has(c.placement.mount.id))continue;
    const p=c.placement,spec=p.spec,url=spec?.visual?.url; if(!url || !fs.existsSync(url.replace(/^\/assets\//,'assets/runtime/')))continue;
    const socket=p.mount.attach==='face'?(p.rear?'rear':'face'):p.mount.attach;
    const turns=(p.quarterTurns+(p.mount.attach==='interior'?interiorArtQuarterTurns(spec.id):0))%4;
    const m=multiply(multiply(GLTF_TO_ZUP,mountRotation(frameOfSocket(spec.attach[0]),socket)),componentMatrix(p.anchor,p.anchorZ/16,turns));
    placements.push({id:'mount:'+c.mount,piece:c.component,role:'equipment',view:c.view,matrix:finalMatrix(m,origin),...asset(url),authored:false,movable:false});
  }
  const facing:any={fore:0,port:1,aft:2,starboard:3};
  for(const o of dressed.objects) {
    if(TEMPLATE_OBJECT_PIECES[o.designId])continue;
    const url=deckObjectVisualUrl(o.designId); if(!url)continue;
    const m=multiply(GLTF_TO_ZUP,componentMatrix([o.at[0]+o.size[0]/2,o.at[1]+o.size[1]/2],.1875,(facing[o.facing]+interiorArtQuarterTurns(o.designId))%4));
    placements.push({id:'socket:'+o.key,piece:o.designId,role:'equipment',view:o.view,matrix:finalMatrix(m,origin),...asset(url),authored:false,movable:false});
  }
  lights=native.lights;
 }
 return {id:doc.id,name:doc.name,revision:doc.revision,theme:doc.theme,catalog:catalog.revision,prefab:JSON.parse(canonical),prefabSha256:sha(canonical),document:prefabConstructionDocument(doc,catalog),placements,lights,
  visualKind:doc.id==='fed.m.wayfarer'?'current-authored-live':'current-authored-template',geometryOrigin:origin,
  palette:doc.id==='fed.m.wayfarer'?study.palette:{...study.palette,...templateKit.palette}};
});
const metadata=['packages/content/src/wayfarer-authored-gameplay.v1.json','packages/content/src/wayfarer-prefab.v1.json','packages/content/src/wayfarer-authored-gameplay.ts','packages/content/src/wayfarer-furnishings.ts','packages/sim/src/ship-dresser.ts','packages/render/src/prefab-ship/frames.ts','packages/render/src/prefab-ship/wayfarer-live-view.ts','packages/render/src/prefab-ship/wayfarer-authored-details.ts','packages/content/src/authored-template-kit.ts','packages/sim/src/authored-template-plan.ts','packages/render/src/prefab-ship/authored-template-view.ts','packages/render/src/prefab-ship/authored-template-objects.ts','packages/render/src/prefab-ship/authored-template-propulsion.ts','packages/render/src/prefab-ship/authored-template-geometry.ts','packages/render/src/prefab-ship/batch.ts','packages/render/src/prefab-ship/surface-attributes.ts',templateManifest,propLightingPath];
const surfaceProbe={positions:[-1,0,0,1,0,0,0,1,0],normals:[0,0,1,0,0,1,0,0,1],indices:[0,1,2],uvs:[0,0,1,0,.5,1],uvs2:[.1,.2,.7,.3,.4,.8],tangents:[1,0,0,1,1,0,0,1,1,0,0,1]};
const identity=Array.from(Matrix.Identity().asArray());
const surfaceContractFixtures=[
 {id:'clip-uv-tangent',matrix:identity,planes:[[1,0,0,0]],profile:null,origin:[0,0]},
 {id:'reflected-affine-normal',matrix:[-2,.2,0,0,0,3,.4,0,0,0,.5,0,1,2,3,1],planes:[],profile:null,origin:[0,0]},
 {id:'profile-jacobian',matrix:identity,planes:[],profile:{bottom:[.2,-.1,2],top:[.4,.1,4]},origin:[3,2]},
 {id:'clip-profile-combined',matrix:identity,planes:[[1,0,0,.25],[0,-1,0,3.75]],profile:{bottom:[.2,-.1,2],top:[.4,.1,4]},origin:[3,2]},
].map(f=>({...f,source:surfaceProbe,expected:clipAuthoredGeometry(surfaceProbe,Matrix.FromArray(f.matrix),f.planes as any,f.profile??undefined,f.origin as [number,number])}));
console.log(JSON.stringify({schema:'sidereal.native-world-assets.v1',sourceRevision:process.env.SIDEREAL_SOURCE_REVISION,sourceRepository:'https://github.com/Dastari/sidereal_spacetime',approval:'Published game assets with canonical runtime clipping/profile adaptation and separately pinned compatibility derivatives where declared; integration is not new art approval.',ships,surfaceContractFixtures,themes:SHIP_THEMES,assets:[...assets.values()].sort((a,b)=>a.file.localeCompare(b.file)),sources:metadata.map(source=>({source,sha256:sha(fs.readFileSync(source)),bytes:fs.statSync(source).size}))}));
'''


def verify() -> None:
    manifest = json.loads((DEST / "manifest.json").read_text())
    assert manifest["schema"] == "sidereal.native-world-assets.v1"
    assert manifest["sourceRevision"] == SOURCE_REVISION
    total = 0
    for entry in manifest["assets"]:
        file = (DEST / entry["file"]).resolve()
        if not file.is_relative_to(DEST.resolve()):
            raise ValueError("Asset path escapes bundle")
        data = file.read_bytes()
        assert len(data) == entry["bytes"], entry["file"]
        assert hashlib.sha256(data).hexdigest() == entry["sha256"], entry["file"]
        total += len(data)
    for ship in manifest["ships"]:
        canonical = json.dumps(ship["prefab"], sort_keys=True, separators=(",", ":"), ensure_ascii=False)
        assert hashlib.sha256(canonical.encode()).hexdigest() == ship["prefabSha256"], ship["id"]
        for placement in ship["placements"]:
            assert any(a["file"] == placement["file"] for a in manifest["assets"])
            assert len(placement["matrix"]) == 16
            if placement.get("clipPlanes"):
                assert all(len(plane) == 4 for plane in placement["clipPlanes"])
    golden_entry = manifest["surfaceGolden"]
    golden_bytes = (DEST / golden_entry["file"]).read_bytes()
    assert len(golden_bytes) == golden_entry["bytes"]
    assert hashlib.sha256(golden_bytes).hexdigest() == golden_entry["sha256"]
    golden = json.loads(golden_bytes)
    assert golden["sourceRevision"] == SOURCE_REVISION and len(golden["fixtures"]) == 4
    print(f"Verified {len(manifest['ships'])} exact public ship documents, {len(manifest['assets'])} assets, {total:,} asset bytes")


def decode_quantized_normals(data: bytes) -> bytes:
    """Godot lacks KHR_mesh_quantization: decode normalized BYTE normals only.

    Keeps every position/index, node, image, material and unrelated buffer byte intact.
    The exact original source hash and derivative hash are recorded separately.
    """
    length = struct.unpack_from("<I", data, 12)[0]
    document = json.loads(data[20:20 + length])
    if "KHR_mesh_quantization" not in document.get("extensionsRequired", []):
        return data
    start = 20 + length
    assert struct.unpack_from("<I", data, start + 4)[0] == 0x004E4942
    binary = bytearray(data[start + 8:start + 8 + struct.unpack_from("<I", data, start)[0]])
    normals = {primitive["attributes"]["NORMAL"] for mesh in document["meshes"] for primitive in mesh["primitives"] if "NORMAL" in primitive["attributes"]}
    for mesh in document["meshes"]:
        for primitive in mesh["primitives"]:
            for name, index in primitive["attributes"].items():
                accessor = document["accessors"][index]
                if accessor["componentType"] != 5126 and name != "NORMAL":
                    raise ValueError("Unsupported quantized attribute conversion")
    for index in normals:
        accessor = document["accessors"][index]
        if accessor["componentType"] == 5126:
            continue
        assert accessor["componentType"] == 5120 and accessor["type"] == "VEC3" and accessor["normalized"]
        view = document["bufferViews"][accessor["bufferView"]]
        offset = view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
        stride = view.get("byteStride", 3)
        decoded = bytearray()
        for row in range(accessor["count"]):
            values = struct.unpack_from("<bbb", binary, offset + row * stride)
            decoded.extend(struct.pack("<fff", *(max(-1.0, value / 127.0) for value in values)))
        while len(binary) % 4:
            binary.append(0)
        offset = len(binary)
        binary.extend(decoded)
        accessor["bufferView"] = len(document["bufferViews"])
        accessor["componentType"] = 5126
        accessor["byteOffset"] = 0
        accessor.pop("normalized")
        accessor.pop("min", None); accessor.pop("max", None)
        document["bufferViews"].append({"buffer": 0, "byteOffset": offset, "byteLength": len(decoded), "target": 34962})
    for key in ["extensionsRequired", "extensionsUsed"]:
        document[key] = [value for value in document.get(key, []) if value != "KHR_mesh_quantization"]
        if not document[key]:
            del document[key]
    document["buffers"][0]["byteLength"] = len(binary)
    metadata = json.dumps(document, separators=(",", ":")).encode()
    metadata += b" " * (-len(metadata) % 4)
    binary += b"\0" * (-len(binary) % 4)
    return struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(metadata) + 8 + len(binary)) + struct.pack("<II", len(metadata), 0x4E4F534A) + metadata + struct.pack("<II", len(binary), 0x004E4942) + binary


def generate(repo: pathlib.Path) -> None:
    paths = ["packages/content", "packages/sim", "packages/render", "assets/runtime/ship-study", "assets/runtime/ship-kit/r002", "assets/runtime/ship-components/r004", "assets/runtime/ship-objects/r001", "tsconfig.json", "package.json"]
    archive = subprocess.check_output(["git", "archive", SOURCE_REVISION, *paths], cwd=repo)
    with tempfile.TemporaryDirectory(prefix="sidereal-native-world-") as temp:
        source = pathlib.Path(temp)
        with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
            tar.extractall(source, filter="data")
        deps = repo / "node_modules"
        if not (deps / ".bin" / "tsx").exists():
            raise RuntimeError("Browser checkout needs its existing locked Node dependencies (npm ci)")
        (source / "node_modules").symlink_to(deps.resolve(), target_is_directory=True)
        (source / "native-world-export.ts").write_text(EXPORT)
        config = {"compilerOptions": {"baseUrl": str(source), "paths": {"@sidereal/content/*": ["packages/content/src/*"], "@sidereal/sim/*": ["packages/sim/src/*"]}}}
        (source / "tsconfig-export.json").write_text(json.dumps(config))
        import os
        env = dict(os.environ, SIDEREAL_SOURCE_REVISION=SOURCE_REVISION)
        output = subprocess.check_output([str(deps / ".bin" / "tsx"), "--tsconfig", str(source / "tsconfig-export.json"), str(source / "native-world-export.ts")], cwd=source, env=env)
        manifest = json.loads(output)
        DEST.mkdir(parents=True, exist_ok=True)
        for entry in manifest["assets"]:
            data = (source / entry["source"]).read_bytes()
            assert hashlib.sha256(data).hexdigest() == entry["sha256"]
            converted = decode_quantized_normals(data)
            if converted != data:
                entry["sourceSha256"] = entry["sha256"]
                entry["sourceBytes"] = len(data)
                entry["compatibilityConversion"] = "Normalized signed-byte NORMAL accessor decoded to float32; source geometry and material data retained"
                data = converted
                entry["sha256"] = hashlib.sha256(data).hexdigest()
                entry["bytes"] = len(data)
            target = DEST / entry["file"]
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        golden = {"schema": "sidereal.native-world-surface-golden.v1", "sourceRevision": SOURCE_REVISION,
                  "scope": "Synthetic numerical contract cases, not renderable game art or world state",
                  "functions": ["clipAuthoredGeometry", "authoredProfilePoint", "appendTransformed", "transformSurfaceFrame"],
                  "sources": [entry for entry in manifest["sources"] if entry["source"].endswith(("authored-template-geometry.ts", "batch.ts", "surface-attributes.ts"))],
                  "fixtures": manifest.pop("surfaceContractFixtures")}
        golden_bytes = (json.dumps(golden, indent=2, ensure_ascii=False) + "\n").encode()
        (DEST / "surface-golden.json").write_bytes(golden_bytes)
        manifest["surfaceGolden"] = {"file": "surface-golden.json", "sha256": hashlib.sha256(golden_bytes).hexdigest(), "bytes": len(golden_bytes)}
        (DEST / "manifest.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n")
        # This directory is the generated public bundle; prune superseded source assets.
        retained = {entry["file"] for entry in manifest["assets"]}
        for previous in DEST.rglob("*.glb"):
            if previous.relative_to(DEST).as_posix() not in retained:
                previous.unlink()
                previous.with_suffix(previous.suffix + ".import").unlink(missing_ok=True)
    verify()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-repo", type=pathlib.Path)
    parser.add_argument("--verify", action="store_true")
    args = parser.parse_args()
    if args.source_repo:
        generate(args.source_repo.resolve())
    else:
        verify()
