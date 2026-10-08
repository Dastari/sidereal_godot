#!/usr/bin/env python3
"""Bundle pinned public space art using the browser's exact reviewed compositors.

No backend publication or existing browser checkout mutation occurs. Normal clones
need only --verify; maintainers regenerate from read-only locked browser sources.
"""
from __future__ import annotations
import argparse, hashlib, io, json, os, pathlib, subprocess, tarfile, tempfile
ROOT = pathlib.Path(__file__).resolve().parents[1]
DEST = ROOT / 'Assets' / 'Environment'
HISTORICAL_REVISION = "e749d5d877ef18ac7421fb003f55effd7e01a404"
SOURCE_REVISION = 'a35632deedf210cf43f64c43cb741d841f4a1ce0'
EXPORT = r'''
import fs from 'node:fs'; import path from 'node:path'; import {createHash} from 'node:crypto';
import {REVIEWED_NATIVE_PLANETS} from './packages/render/src/environment/reviewed-native-planet-catalog.ts';
import {decodeReviewedNativePayload} from './packages/render/src/environment/reviewed-native-planet-encoding.ts';
import {validateModernNativePlanetKit} from './packages/render/src/environment/modern-native-planet-schema.ts';
import {validateReviewedWeather} from './packages/render/src/environment/reviewed-native/reviewed-weather-validation.ts';
import {composeReviewedPlanet} from './packages/render/src/environment/reviewed-native/build-reviewed-planet.ts';
import {planetRecipe,planetEffects,SPACE_VISTAS,DEFAULT_SPACE_VISTA} from './packages/content/src/environment.ts';
import {SOLAR_SYSTEM} from './packages/content/src/solar-system.ts';
import {distantStarCatalog} from './packages/render/src/environment/starfield.ts';
import {STUDIO_ENVIRONMENT,studioEnvironmentHdr,SURFACE_FINISHES,MOLDED_LIGHTING,MOLDED_GRADING,shipSlotFamily,crewSlotFamily} from './packages/render/src/molded-plastic.ts';
import {prefabNozzleLayout} from './packages/render/src/prefab-ship/exhaust.ts';
import {PREFAB_SHIPS} from './packages/content/src/prefabs/index.ts';
import {defaultPrefabComponentCatalog} from './packages/content/src/ship-prefab-catalog.ts';
import {meshBoxes,newBuilder} from './packages/render/src/prefab-ship/box-mesher.ts';
import {emitPlume} from './packages/render/src/prefab-ship/component-standins.ts';
import {shipLogicModel} from './packages/sim/src/ship-logic-model.ts';
import {prefabEvaModel} from './packages/sim/src/eva.ts';
import {canonicalShipPrefabJson,prefabOrigin,volumeGeometry} from './packages/content/src/ship-prefab.ts';
import {rasterOutline} from './packages/sim/src/ship-dresser.ts';
import {SHIP_KIT_SLOTS} from './packages/content/src/ship-kit.ts';
import {SHIP_THEMES} from './packages/content/src/ship-themes.ts';
import {prefabFrameMatrix,transformPoint,transformDirection} from './packages/render/src/prefab-ship/frames.ts';
import {stellarEruptionState} from './packages/render/src/environment/stellar-eruption.ts';
import {dustCell,dustLayout,dustMotion,dustDepthLayers} from './packages/render/src/environment/dust.ts';
const dest=process.env.SIDEREAL_SPACE_DEST!;fs.mkdirSync(dest,{recursive:true});
const sha=(b:Buffer)=>createHash('sha256').update(b).digest('hex');
const previous=process.env.SIDEREAL_METADATA_ONLY?JSON.parse(fs.readFileSync(path.join(dest,'manifest.json'),'utf8')):null;
const assets=new Map<string,any>((previous?.assets??[]).map((a:any)=>[a.file,a]));const textureNames=new Map<string,string>();
const write=(file:string,bytes:Buffer)=>{const target=path.join(dest,file);fs.mkdirSync(path.dirname(target),{recursive:true});fs.writeFileSync(target,bytes);assets.set(file,{file,bytes:bytes.length,sha256:sha(bytes)});return file;};
function copy(source:string,file:string){const b=fs.readFileSync(source);write(file,b);return {file,source,sourceSha256:sha(b),sourceBytes:b.length};}
function texture(base:string,file:string){const source=path.join('assets/reviewed-celestials',base.replace(/^\//,''),file),bytes=fs.readFileSync(source),hash=sha(bytes);const target='Textures/'+hash+path.extname(file);if(!assets.has(target))copy(source,target);textureNames.set(source,target);return target;}
function roleTextures(role:any,base:string){const out={...role};for(const key of ['baseColorTexture','normalTexture','emissiveTexture','clearcoatNormalTexture','metallicRoughnessTexture'])if(role[key])out[key]=texture(base,role[key]);return out;}
function glb(batches:any[],roles:any[],file:string){
 const bin:Buffer[]=[],views:any[]=[],accessors:any[]=[],images:any[]=[],textures:any[]=[],materials:any[]=[];let size=0;
 function attr(values:any,type:string,integer=false){const count=values.length/(type==='VEC3'?3:type==='VEC2'?2:type==='VEC4'?4:1);if(!Number.isInteger(count)||!values.length)throw Error('Invalid outputattribute');const ar=integer?Uint32Array.from(values):Float32Array.from(values);const bytes=Buffer.from(ar.buffer,ar.byteOffset,ar.byteLength);const view=views.length;views.push({buffer:0,byteOffset:size,byteLength:bytes.length,target:integer?34963:34962});bin.push(bytes);size+=bytes.length;const a:any={bufferView:view,componentType:integer?5125:5126,count,type};if(type==='VEC3'&&!integer){const lo=[Infinity,Infinity,Infinity],hi=[-Infinity,-Infinity,-Infinity];for(let i=0;i<values.length;i++) {if(!Number.isFinite(values[i]))throw Error('Nonfiniteoutput');lo[i%3]=Math.min(lo[i%3],ar[i]);hi[i%3]=Math.max(hi[i%3],ar[i]);}a.min=lo;a.max=hi;}accessors.push(a);return accessors.length-1;}
 function tex(file:string){const index=images.findIndex(v=>v.uri==='../'+file);if(index>=0)return {index};images.push({uri:'../'+file});textures.push({source:images.length-1,sampler:0});return {index:textures.length-1};}
 for(const role of roles){const m:any={name:role.name,pbrMetallicRoughness:{baseColorFactor:[...role.linearColor.slice(0,3),role.alpha??1],metallicFactor:role.metallic??0,roughnessFactor:role.roughness??1},alphaMode:role.alphaMode??'OPAQUE',doubleSided:role.doubleSided??false,extras:{nativeRole:role}};
 if(role.baseColorTexture)m.pbrMetallicRoughness.baseColorTexture=tex(role.baseColorTexture);if(role.metallicRoughnessTexture)m.pbrMetallicRoughness.metallicRoughnessTexture=tex(role.metallicRoughnessTexture);if(role.normalTexture)m.normalTexture={...tex(role.normalTexture),scale:role.normalScale??1};if(role.emissiveTexture)m.emissiveTexture=tex(role.emissiveTexture);if(role.emissiveColor)m.emissiveFactor=role.emissiveColor.slice(0,3);if(role.alphaCutoff!==undefined)m.alphaCutoff=role.alphaCutoff;
 const ext:any={};if(role.emissiveStrength!==undefined)ext.KHR_materials_emissive_strength={emissiveStrength:role.emissiveStrength};const coat=role.clearcoatFactor??role.clearCoat?.intensity;if(coat)ext.KHR_materials_clearcoat={clearcoatFactor:coat,clearcoatRoughnessFactor:role.clearcoatRoughnessFactor??role.clearCoat?.roughness??0};if(role.ior)ext.KHR_materials_ior={ior:role.ior};if(role.transmissionFactor)ext.KHR_materials_transmission={transmissionFactor:role.transmissionFactor};if(Object.keys(ext).length)m.extensions=ext;materials.push(m);}
 function weld(b:any){const channels=['positions','normals','uvs','colors'].filter(k=>b[k]?.length);const stride:any={positions:3,normals:3,uvs:2,colors:4},out:any=Object.fromEntries(channels.map(k=>[k,[]])),seen=new Map<string,number>(),map:number[]=[];for(let i=0;i<b.positions.length/3;i++){const values=channels.flatMap(k=>Array.from(b[k].slice(i*stride[k],(i+1)*stride[k])).map(Math.fround));const key=values.join(',');let index=seen.get(key);if(index===undefined){index=seen.size;seen.set(key,index);let at=0;for(const k of channels){out[k].push(...values.slice(at,at+stride[k]));at+=stride[k];}}map[i]=index;}return {...b,...out,indices:Array.from(b.indices).map((i:any)=>map[i])};}
 const primitives:any[]=[];let triangles=0,vertices=0;const ranges:any[]=[];
 for(let i=0;i<batches.length;i++){const original=batches[i];if(!original.indices.length)continue;const b=weld(original);if(!b.indices.length)continue;const attributes:any={POSITION:attr(b.positions,'VEC3'),NORMAL:attr(b.normals,'VEC3')};if(b.uvs?.length)attributes.TEXCOORD_0=attr(b.uvs,'VEC2');if(b.colors?.length)attributes.COLOR_0=attr(b.colors,'VEC4');primitives.push({attributes,indices:attr(b.indices,'SCALAR',true),material:i,mode:4});triangles+=b.indices.length/3;vertices+=b.positions.length/3;ranges.push({material:i,ranges:b.ranges??[]});}
 if(!primitives.length)return null;
 const extensionsUsed=[...new Set(materials.flatMap(m=>Object.keys(m.extensions??{})))];const doc:any={asset:{version:'2.0',generator:'Sidereal exact reviewed browser composition adapter'},scene:0,scenes:[{nodes:[0]}],nodes:[{name:'ReviewedAuthoredCelestial',mesh:0}],meshes:[{primitives}],buffers:[{byteLength:size}],bufferViews:views,accessors,materials,samplers:[{magFilter:9729,minFilter:9987,wrapS:10497,wrapT:10497}],images,textures};if(extensionsUsed.length)doc.extensionsUsed=extensionsUsed;
 let metadata=Buffer.from(JSON.stringify(doc));metadata=Buffer.concat([metadata,Buffer.alloc((4-metadata.length%4)%4,32)]);const binary=Buffer.concat(bin);const header=Buffer.alloc(12),jc=Buffer.alloc(8),bc=Buffer.alloc(8);header.writeUInt32LE(0x46546c67);header.writeUInt32LE(2,4);header.writeUInt32LE(12+8+metadata.length+8+binary.length,8);jc.writeUInt32LE(metadata.length);jc.writeUInt32LE(0x4e4f534a,4);bc.writeUInt32LE(binary.length);bc.writeUInt32LE(0x004e4942,4);write(file,Buffer.concat([header,jc,metadata,bc,binary]));return {file,triangles,vertices,ranges};
}
const bodies:any[]=previous?.bodies??[];
for(const d of (previous?[]:REVIEWED_NATIVE_PLANETS)){const source='assets/reviewed-celestials'+d.kitURL,bytes=fs.readFileSync(source);if(sha(bytes)!==d.runtimeKitSha256)throw Error('Kitpinchanged '+d.id);const kit=validateModernNativePlanetKit(decodeReviewedNativePayload(bytes,d.encoding),bytes.length);let weather:any=null;if(d.weather){const b=fs.readFileSync('assets/reviewed-celestials'+d.weather.kitURL);if(sha(b)!==d.weather.runtimeKitSha256)throw Error('Weatherpinchanged');weather=validateReviewedWeather(decodeReviewedNativePayload(b,d.weather.encoding),b.length);}
 const recipe=planetRecipe(d.style as any,117),roles=kit.materials.map(r=>roleTextures(r,d.textureBaseURL));const weatherRole=weather?roleTextures(weather.materials[0],d.weather!.textureBaseURL):null;
 const levels:any[]=[];for(const lod of (d.fixedDetail?[0]:[0,1,2])){const output=composeReviewedPlanet(kit,weather,{bodyId:'public-composition:'+d.id,seed:117,lod:lod as any,recipe});const prefix='Meshes/'+d.id+'-s117-l'+lod;
 const surface=glb(output.batches,roles,prefix+'.glb');let weatherMesh=null,smokeMesh=null;if(output.weather){const role=weatherRole?.alphaMode==='BLEND'?weatherRole:{...weatherRole,name:'Native cloud weather',metallic:0,roughness:.95};weatherMesh=glb([output.weather],[role],prefix+'-weather.glb');}if(output.smoke){smokeMesh=glb([output.smoke],[{name:'Original volcanic smoke',linearColor:[1,1,1],roughness:1,metallic:0,alpha:.48,alphaMode:'BLEND'}],prefix+'-smoke.glb');}
 levels.push({lod,surface,weather:weatherMesh,smoke:smokeMesh,shadowRadii:output.shadowRadii,weatherShadowRadius:output.weatherShadowRadius});console.error('Composed '+d.id+' L'+lod+' '+surface?.triangles+' triangles');}
 bodies.push({id:d.id,seed:117,style:d.style,fixedDetail:d.fixedDetail,glow:d.glow,localLight:d.localLight,sourceKitSha256:d.sourceKitSha256,runtimeKitSha256:d.runtimeKitSha256,recipe,effects:planetEffects(recipe),roles,weatherRole,levels});}
const copied=[copy('assets/reviewed-celestials/reviewed-stars/yellow-main-sequence/star.glb','star.glb'),copy('assets/runtime/environment/veil-nebula-v1.png','veil-nebula-v1.png'),copy('assets/runtime/environment/orion-veil-v1.png','orion-veil-v1.png'),copy('assets/runtime/materials/frontier-workshop.hdr','frontier-workshop.hdr'),copy('assets/runtime/voxels/asteroid.glb','asteroid.glb')];write('molded-studio.hdr',Buffer.from(studioEnvironmentHdr()));
const plume=newBuilder(),colors:number[]=[];emitPlume(plume,colors,[0,0,0],1,1);const positions:number[]=[],normals:number[]=[];for(let i=0;i<plume.positions.length;i+=3){positions.push(plume.positions[i],plume.positions[i+2],-plume.positions[i+1]);normals.push(plume.normals[i],plume.normals[i+2],-plume.normals[i+1]);}
const historical=JSON.parse(fs.readFileSync(path.join(dest,'historical-gameplay.json'),'utf8'));assets.set('historical-gameplay.json',{file:'historical-gameplay.json',bytes:fs.statSync(path.join(dest,'historical-gameplay.json')).size,sha256:sha(fs.readFileSync(path.join(dest,'historical-gameplay.json')))});
const catalog=defaultPrefabComponentCatalog();write('gameplay-geometry.json',Buffer.from(JSON.stringify({schema:'sidereal.native-gameplay-geometry.v1',sourceRevision:process.env.SIDEREAL_SOURCE_REVISION,catalog:catalog.revision,ships:PREFAB_SHIPS.map(doc=>({id:doc.id,revision:doc.revision,prefabSha256:sha(Buffer.from(canonicalShipPrefabJson(doc))),logic:((m)=>m?{...m,graph:{devices:[...m.graph.devices.values()],wires:Object.fromEntries(m.graph.wires)}}:null)(shipLogicModel(doc,catalog)),eva:prefabEvaModel(doc,catalog)})).concat(historical.ships)})));const nozzles=PREFAB_SHIPS.map(d=>({prefabId:d.id,revision:d.revision,nozzles:prefabNozzleLayout(d,catalog)})).concat(historical.nozzles);
const proxies=PREFAB_SHIPS.map(doc=>{const boxes=doc.volumes.flatMap(v=>{const g=volumeGeometry(v);return g.outline?rasterOutline(g.outline,g.z,()=> 'primary',64):[];});const slots=meshBoxes(boxes,{chamfer:0}).slots;const frame=prefabFrameMatrix(prefabOrigin(doc));const batches=slots.map(s=>{const positions:number[]=[],normals:number[]=[];for(let i=0;i<s.positions.length;i+=3){positions.push(...transformPoint(frame,[s.positions[i],s.positions[i+1],s.positions[i+2]]));normals.push(...transformDirection(frame,[s.normals[i],s.normals[i+1],s.normals[i+2]]));}return {...s,positions,normals};});const roles=slots.map(s=>{const r=SHIP_THEMES[doc.theme].slots[SHIP_KIT_SLOTS[s.slot]];return {name:SHIP_KIT_SLOTS[s.slot],linearColor:r.colour,roughness:r.roughness,metallic:r.metallic};});return {prefabId:doc.id,revision:doc.revision,...glb(batches,roles,'Proxies/'+doc.id+'-r'+doc.revision+'.glb')};});
for(const p of historical.proxies??[])proxies.push({prefabId:p.prefabId,revision:p.revision,...glb(p.batches,p.roles,'Proxies/'+p.prefabId+'-r'+p.revision+'.glb')});
const stars=distantStarCatalog();const golden={eruptions:[0,4,7,10,22,25,46].map(time=>({time,result:stellarEruptionState(time)})),depthLayers:[{camera:{x:0,y:216,z:3},target:{x:0,y:0,z:0},aspect:16/9},{camera:{x:40,y:30,z:15},target:{x:2,y:1,z:-5},aspect:.6}].map(v=>({...v,result:dustDepthLayers(v.camera,v.target,.5,v.aspect)})),stars:stars.slice(0,8),dust:[0,1,30,575].map(index=>({index,originX:1e12+.125,originY:-1e12-.25,spacing:8,...dustCell(index,1e12+.125,-1e12-.25,8)})),dustLayout:[{half:55,aspect:16/9,result:dustLayout(55,16/9)},{half:650,aspect:.6,result:dustLayout(650,.6)}],dustMotion:[0,100,600,3000].map(speed=>({speed,result:dustMotion(speed,0,false)}))};write('golden.json',Buffer.from(JSON.stringify(golden)));
const sourceFiles=fs.readdirSync('packages/render/src/environment',{recursive:true}).filter(f=>typeof f==='string'&&f.endsWith('.ts')).map(f=>'packages/render/src/environment/'+f).concat(['packages/content/src/environment.ts','packages/content/src/solar-system.json','packages/render/src/molded-plastic.ts','packages/render/src/prefab-ship/exhaust.ts','packages/render/src/prefab-ship/component-standins.ts','packages/sim/src/space-background.ts']);
const surfaceSlots=['primary','secondary','accent','trim','metal','dark','emit_a','emit_b','glass'],crewSlots=['skin','hair','eye','face','suit_primary','suit_secondary','accent','metal','dark','emit','glass'];
const surfaceFinish={families:SURFACE_FINISHES,lighting:MOLDED_LIGHTING,grading:MOLDED_GRADING,ships:Object.fromEntries(surfaceSlots.map(s=>[s,shipSlotFamily(s)])),crew:Object.fromEntries(['body','armour','head'].map(part=>[part,Object.fromEntries(crewSlots.map(s=>[s,crewSlotFamily(s,part as any)]))]))};
const manifest={schema:'sidereal.native-space-assets.v1',sourceRevision:process.env.SIDEREAL_SOURCE_REVISION,sourceRepository:'https://github.com/Dastari/sidereal_spacetime',scope:'Exact reviewed browser composition derivatives for canonical seed117; accepted live projections own position, radius and disclosure. This adapter grants no new art approval.',bodies,star:{id:'yellow-main-sequence-r013',seed:3901,file:'star.glb',visualRadiusScale:2.1,sha256:copied[0].sourceSha256},stars,vistas:SPACE_VISTAS,defaultVista:DEFAULT_SPACE_VISTA,studioEnvironment:STUDIO_ENVIRONMENT,surfaceFinish,chart:SOLAR_SYSTEM.bodies.map(({id,key,name,appearance,seed})=>({id,key,name,appearance,seed})),nozzles,proxies,plume:{positions,normals,indices:plume.indices,colors},assets:[...assets.values()].sort((a,b)=>a.file.localeCompare(b.file)),sources:sourceFiles.sort().map(file=>({file,sha256:sha(fs.readFileSync(file))})),originalAssets:copied};fs.writeFileSync(path.join(dest,'manifest.json'),JSON.stringify(manifest,null,2)+'\n');
'''

def verify():
    m=json.loads((DEST/'manifest.json').read_text());assert m['schema']=='sidereal.native-space-assets.v1';assert m['sourceRevision']==SOURCE_REVISION
    assert len(m['bodies'])==28 and len(m['stars'])==8192 and len(m['chart'])==29
    total=0
    for a in m['assets']:
        p=(DEST/a['file']).resolve();assert p.is_relative_to(DEST.resolve());b=p.read_bytes();assert len(b)==a['bytes'];assert hashlib.sha256(b).hexdigest()==a['sha256'];total+=len(b)
    for body in m['bodies']:
        assert body['seed']==117 and len(body['levels'])==(1 if body['fixedDetail'] else 3)
        for level in body['levels']:
            assert level['surface'] and any(a['file']==level['surface']['file'] for a in m['assets'])
    print(f"Verified28pinnedplanets,29publicchartnames,8192stable stars,{len(m['assets'])} assets,{total:,}bytes")

HISTORY_EXPORT = r'''
import fs from 'node:fs';import {createHash} from 'node:crypto';
import {PREFAB_SHIPS} from './packages/content/src/prefabs/index.ts';
import {defaultPrefabComponentCatalog} from './packages/content/src/ship-prefab-catalog.ts';
import {canonicalShipPrefabJson,prefabOrigin,volumeGeometry} from './packages/content/src/ship-prefab.ts';
import {meshBoxes} from './packages/render/src/prefab-ship/box-mesher.ts';import {rasterOutline} from './packages/sim/src/ship-dresser.ts';import {SHIP_KIT_SLOTS} from './packages/content/src/ship-kit.ts';import {SHIP_THEMES} from './packages/content/src/ship-themes.ts';import {prefabFrameMatrix,transformPoint,transformDirection} from './packages/render/src/prefab-ship/frames.ts';
import {prefabNozzleLayout} from './packages/render/src/prefab-ship/exhaust.ts';
import {shipLogicModel} from './packages/sim/src/ship-logic-model.ts';import {prefabEvaModel} from './packages/sim/src/eva.ts';
const doc=PREFAB_SHIPS.find(d=>d.id==='fed.m.wayfarer')!,catalog=defaultPrefabComponentCatalog(),logic=shipLogicModel(doc,catalog);if(doc.revision!==1)throw Error('HistoricalWayfarerrevisionchanged');
const boxes=doc.volumes.flatMap(v=>{const g=volumeGeometry(v);return g.outline?rasterOutline(g.outline,g.z,()=> 'primary',64):[];});const slots=meshBoxes(boxes,{chamfer:0}).slots,frame=prefabFrameMatrix(prefabOrigin(doc));const batches=slots.map(s=>{const positions:number[]=[],normals:number[]=[];for(let i=0;i<s.positions.length;i+=3){positions.push(...transformPoint(frame,[s.positions[i],s.positions[i+1],s.positions[i+2]]));normals.push(...transformDirection(frame,[s.normals[i],s.normals[i+1],s.normals[i+2]]));}return {positions,normals,indices:Array.from(s.indices)};});const roles=slots.map(s=>{const r=SHIP_THEMES[doc.theme].slots[SHIP_KIT_SLOTS[s.slot]];return{name:SHIP_KIT_SLOTS[s.slot],linearColor:r.colour,roughness:r.roughness,metallic:r.metallic};});
console.log(JSON.stringify({schema:'sidereal.native-historical-gameplay.v1',proxies:[{prefabId:doc.id,revision:doc.revision,batches,roles}],sourceRevision:process.env.SIDEREAL_SOURCE_REVISION,ships:[{id:doc.id,revision:doc.revision,prefabSha256:createHash('sha256').update(canonicalShipPrefabJson(doc)).digest('hex'),logic:logic?{...logic,graph:{devices:[...logic.graph.devices.values()],wires:Object.fromEntries(logic.graph.wires)}}:null,eva:prefabEvaModel(doc,catalog)}],nozzles:[{prefabId:doc.id,revision:doc.revision,nozzles:prefabNozzleLayout(doc,catalog)}]}));
'''

def generate_history(repo:pathlib.Path):
    with tempfile.TemporaryDirectory(prefix='sidereal-native-space-history-') as t:
        source=pathlib.Path(t);paths=['packages/content','packages/sim','packages/render','tsconfig.json','package.json']
        archive=subprocess.Popen(['git','archive',HISTORICAL_REVISION,*paths],cwd=repo,stdout=subprocess.PIPE)
        with tarfile.open(fileobj=archive.stdout,mode='r|') as tar:tar.extractall(source,filter='data')
        assert archive.wait()==0
        deps=repo/'node_modules';(source/'node_modules').symlink_to(deps.resolve(),target_is_directory=True)
        (source/'history-export.ts').write_text(HISTORY_EXPORT)
        (source/'tsconfig-export.json').write_text(json.dumps({'compilerOptions':{'baseUrl':str(source),'paths':{'@sidereal/content/*':['packages/content/src/*'],'@sidereal/sim/*':['packages/sim/src/*']}}}))
        output=subprocess.check_output([str(deps/'.bin/tsx'),'--tsconfig',str(source/'tsconfig-export.json'),str(source/'history-export.ts')],cwd=source,env=dict(os.environ,SIDEREAL_SOURCE_REVISION=HISTORICAL_REVISION))
        DEST.mkdir(parents=True,exist_ok=True);(DEST/'historical-gameplay.json').write_bytes(output)

def generate(repo:pathlib.Path,metadata_only=False):
    generate_history(repo)
    paths=['packages/content','packages/sim','packages/render','assets/reviewed-celestials','assets/runtime/environment','assets/runtime/wayfarer-access','assets/runtime/ship-access','assets/runtime/materials','assets/runtime/voxels/asteroid.glb','tsconfig.json','package.json']
    with tempfile.TemporaryDirectory(prefix='sidereal-native-space-') as t:
        source=pathlib.Path(t)
        archive=subprocess.Popen(['git','archive',SOURCE_REVISION,*paths],cwd=repo,stdout=subprocess.PIPE)
        with tarfile.open(fileobj=archive.stdout,mode='r|') as tar:tar.extractall(source,filter='data')
        assert archive.wait()==0
        deps=repo/'node_modules';assert (deps/'.bin/tsx').exists(),'Read-onlybrowserlocked dependencies required'
        (source/'node_modules').symlink_to(deps.resolve(),target_is_directory=True)
        (source/'native-space-export.ts').write_text(EXPORT)
        config={'compilerOptions':{'baseUrl':str(source),'paths':{'@sidereal/content/*':['packages/content/src/*'],'@sidereal/sim/*':['packages/sim/src/*']}}}
        (source/'tsconfig-export.json').write_text(json.dumps(config))
        subprocess.run([str(deps/'.bin/tsx'),'--tsconfig',str(source/'tsconfig-export.json'),str(source/'native-space-export.ts')],cwd=source,check=True,env=dict(os.environ,SIDEREAL_SOURCE_REVISION=SOURCE_REVISION,SIDEREAL_SPACE_DEST=str(DEST),NODE_OPTIONS='--max-old-space-size=6144',**({'SIDEREAL_METADATA_ONLY':'1'} if metadata_only else {})))
    verify()

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--source-repo',type=pathlib.Path);p.add_argument('--verify',action='store_true');p.add_argument('--metadata-only',action='store_true');a=p.parse_args()
    if a.verify:verify()
    elif a.source_repo:generate(a.source_repo.resolve(),a.metadata_only)
    else:p.error('Pass --verify or --source-repo')
