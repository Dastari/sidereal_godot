#!/usr/bin/env python3
"""Bundle immutable browser crew assets and export its exact semantic presentation maps."""
from __future__ import annotations
import argparse, hashlib, json, os, pathlib, struct, subprocess, tarfile, tempfile, math, bisect
ROOT=pathlib.Path(__file__).resolve().parents[1]
DEST=ROOT/'Assets/Crew'
REVISION='a35632deedf210cf43f64c43cb741d841f4a1ce0'
EXPORT=r'''
import fs from 'node:fs';
import {decodeCrewFacePng,composeCrewFace,hexToRgb255} from './packages/render/src/crew/crew-study-face.ts';
import {CREW_STUDY,CREW_STUDY_SCALE,crewStudyEquipment,crewStudyHair} from './packages/content/src/crew-study.ts';
import {INVENTORY_DEFINITIONS} from './packages/content/src/inventory.ts';
import {CREW_ITEMS,crewArmedClass} from './packages/content/src/crew-items.ts';
import {CREW_WARDROBE} from './packages/content/src/crew-wardrobe.ts';
import {resolveCrewAppearance} from './packages/render/src/crew/appearance.ts';
import {VOXEL_CREW_UPPER_BONES,VOXEL_CREW_CLIP_FALLBACK,VOXEL_CREW_NOMINAL_SPEED} from './packages/content/src/crew-voxel-bundle.ts';
import {PREFAB_SHIPS} from './packages/content/src/prefabs/index.ts';
import {defaultPrefabComponentCatalog} from './packages/content/src/ship-prefab-catalog.ts';
import {canonicalShipPrefabJson,prefabOrigin} from './packages/content/src/ship-prefab.ts';
import {dressShip} from './packages/sim/src/ship-dresser.ts';
import {prefabBedSeats} from './packages/sim/src/prefab-seats.ts';
import {prefabSeatPresentation} from './packages/render/src/crew/seat-presentation.ts';
import {createHash} from 'node:crypto';
import {WALK_SPEED_MPS,SPRINT_SPEED_MPS} from './packages/sim/src/index.ts';
import {NullEngine} from '@babylonjs/core/Engines/nullEngine';
import {Scene} from '@babylonjs/core/scene';
import {TransformNode} from '@babylonjs/core/Meshes/transformNode';
import {Matrix,Quaternion,Vector3} from '@babylonjs/core/Maths/math.vector';
import {Logger} from '@babylonjs/core/Misc/logger';
import {createFootPlanting,solveTwoBone,leanSeatSpine} from './packages/render/src/crew/voxel-ik.ts';
const equipment=Object.fromEntries(INVENTORY_DEFINITIONS.filter(d=>d.equipSlot&&crewStudyEquipment(d.equipSlot,d.id)).map(d=>[d.id,{slot:d.equipSlot,...crewStudyEquipment(d.equipSlot!,d.id)}]));
const definitions=Object.fromEntries(INVENTORY_DEFINITIONS.map(d=>[d.id,{slot:d.equipSlot,crewItemId:d.crewItemId,characterComponentId:d.characterComponentId,wardrobeId:d.wardrobeId,pose:d.pose}]));
const catalog=defaultPrefabComponentCatalog();const historical=JSON.parse(fs.readFileSync(process.env.SIDEREAL_CREW_WORLD_MANIFEST!,'utf8')).ships.filter((s:any)=>s.id==='fed.m.wayfarer'&&s.revision===1&&s.prefabSha256==='b71bf73252a2884abb557bc23d9d9043872372768743df7315b6548431dac9d3').map((s:any)=>s.document.prefab.document);const seats=[...PREFAB_SHIPS,...historical].map(doc=>{const [ox,oy]=prefabOrigin(doc);const points=prefabBedSeats(doc,catalog).map(b=>({x:b.seatX,y:b.seatY,centerX:b.x,centerY:b.y,placementId:b.placementId,objectId:b.placementId.startsWith("prefab:socket:")?b.placementId.slice(14):null})).concat(dressShip(doc,{catalog}).components.filter(c=>c.component==='console.navigation.sm'&&c.placement.mount.attach==='interior').map(c=>({x:-(c.placement.anchor[1]-oy),y:c.placement.anchor[0]-ox})));return {id:doc.id,revision:doc.revision,prefabSha256:createHash('sha256').update(canonicalShipPrefabJson(doc)).digest('hex'),points:points.map(p=>({...p,contact:prefabSeatPresentation({doc,catalog},p.x,p.y)})).filter(p=>p.contact)};});
const faceGoldens=[];for(const variant of ['m_classic','f_classic'])for(const state of [{expression:'neutral',detail:'none',age:'none'},{expression:'determined',detail:'scars',age:'lines',blinkEyes:'closed'}]){const def=CREW_STUDY.face[variant];const base='assets/runtime/crew/'+CREW_STUDY.revision+'/';const atlas=JSON.parse(fs.readFileSync(base+def.json,'utf8'));const image=await decodeCrewFacePng(fs.readFileSync(base+def.png));const colors={skin:[187,128,94],hair:[71,49,44],eye:[45,158,213]} as any;const pixels=composeCrewFace(atlas,image,state,colors);faceGoldens.push({variant,state,colors,cell:atlas.cell,sha256:createHash('sha256').update(pixels).digest('hex')});}
// The actual browser IK functions run against unchanged raw clip transforms. These
// are independent source fixtures, not values produced by the native implementation.
Logger.LogLevels=Logger.NoneLogLevel;const engine=new NullEngine();const scene=new Scene(engine);scene.useRightHandedSystem=true;
const frame=new TransformNode('fixture.ship',scene),actor=new TransformNode('fixture.actor',scene),visual=new TransformNode('fixture.visual',scene),rig=new TransformNode('fixture.rig',scene);actor.parent=frame;visual.parent=actor;visual.scaling.setAll(CREW_STUDY_SCALE);rig.parent=visual;
const raw=fs.readFileSync('assets/runtime/crew/'+CREW_STUDY.revision+'/'+CREW_STUDY.animation.file),jsonLength=raw.readUInt32LE(12),doc=JSON.parse(raw.subarray(20,20+jsonLength).toString()),binary=28+jsonLength;
const joints=doc.skins[0].joints as number[],boneNodes=new Map<number,TransformNode>(),byName=new Map<string,TransformNode>(),parentByNode=new Map<number,number>();for(let n=0;n<doc.nodes.length;n++)for(const child of doc.nodes[n].children??[])parentByNode.set(child,n);
for(const i of joints){const n=new TransformNode(doc.nodes[i].name,scene);boneNodes.set(i,n);byName.set(n.name,n);}for(const [i,n] of boneNodes)n.parent=boneNodes.get(parentByNode.get(i)!)??rig;
const floats=(index:number)=>{const a=doc.accessors[index],view=doc.bufferViews[a.bufferView],count=({SCALAR:1,VEC3:3,VEC4:4} as any)[a.type],stride=view.byteStride??count*4,offset=binary+(view.byteOffset??0)+(a.byteOffset??0);return Array.from({length:a.count},(_,i)=>Array.from({length:count},(_,j)=>raw.readFloatLE(offset+i*stride+j*4)));};const accessorCache=new Map<number,number[][]>();const access=(i:number)=>{if(!accessorCache.has(i))accessorCache.set(i,floats(i));return accessorCache.get(i)!;};
const sample=(clip:string,time:number)=>{for(const [i,n]of boneNodes){n.position=Vector3.FromArray(doc.nodes[i].translation??[0,0,0]);n.rotationQuaternion=Quaternion.FromArray(doc.nodes[i].rotation??[0,0,0,1]);n.scaling=Vector3.FromArray(doc.nodes[i].scale??[1,1,1]);}const animation=doc.animations.find((a:any)=>a.name===clip),duration=Math.max(...animation.samplers.map((s:any)=>access(s.input).at(-1)![0]));time=CREW_STUDY.clips[clip].loop?Math.max(0,time)%duration:Math.max(0,Math.min(time,duration));for(const c of animation.channels){const s=animation.samplers[c.sampler],ts=access(s.input),vs=access(s.output);let i=0;while(i+1<ts.length&&ts[i+1][0]<=time)i++;const j=Math.min(i+1,ts.length-1),t=i===j?0:(time-ts[i][0])/(ts[j][0]-ts[i][0]),n=boneNodes.get(c.target.node)!;if(c.target.path==='rotation')n.rotationQuaternion=Quaternion.Slerp(Quaternion.FromArray(vs[i]),Quaternion.FromArray(vs[j]),t).normalize();else{const value=Vector3.Lerp(Vector3.FromArray(vs[i]),Vector3.FromArray(vs[j]),t);if(c.target.path==='translation')n.position=value;else n.scaling=value;}}for(const n of boneNodes.values())n.computeWorldMatrix(true);};
const bones=()=>Object.fromEntries([...byName].map(([name,n])=>[name,{position:n.position.asArray(),rotation:n.rotationQuaternion!.asArray(),scale:n.scaling.asArray()}]));const legs=['L','R'].map(side=>({root:rig,upper:byName.get('thigh.'+side)!,lower:byName.get('shin.'+side)!,end:byName.get('foot.'+side)!,effector:byName.get('foot.'+side)!}));
const planting=createFootPlanting(visual,frame,legs,{restAnkle:3/32,maxDrift:.12*CREW_STUDY_SCALE});const kinematicsGoldens:any[]=[];const steps:any[]=[];
const footStep=(clip:string,time:number,position:number[],yaw:number,dt:number,plant:boolean)=>{sample(clip,time);actor.position=Vector3.FromArray(position);actor.rotationQuaternion=Quaternion.RotationAxis(Vector3.Up(),yaw);planting.step(dt,plant);const input={clip,time,position,yaw,dt,plant};steps.push(input);return input;};
for(let i=0;i<90;i++){const t=i/120;const input=footStep('walk',.08+t*(WALK_SPEED_MPS/VOXEL_CREW_NOMINAL_SPEED.walk/CREW_STUDY_SCALE),[0,0,-WALK_SPEED_MPS*t],0,1/120,true);if([0,30,60,89].includes(i))kinematicsGoldens.push({kind:'feet',step:i,input,error:planting.error,bones:bones()});}
for(let i=90;i<117;i++){const input=footStep('idle',(i-90)/120,[0,0,-WALK_SPEED_MPS*89/120],.37,1/120,false);if(i===116)kinematicsGoldens.push({kind:'feet',step:i,input,error:planting.error,bones:bones()});}
for(let i=117;i<124;i++){const input=footStep('walk',.16+(i-117)/120,[5,0,-2],-.29,1/120,true);if(i===123)kinematicsGoldens.push({kind:'feet',step:i,input,error:planting.error,bones:bones()});}
for(const contact of [{lift:.22,lean:.14,footSupport:0,forward:.08,footForward:.45},{lift:.1,lean:-.05,footSupport:.06,forward:0}]){sample('sit_idle',1.25);actor.position.set(0,contact.lift,0);actor.rotationQuaternion=Quaternion.RotationAxis(Vector3.Up(),.37);visual.position.z=-contact.forward;leanSeatSpine(visual,byName.get('spine')!,contact.lean);const inverse=actor.computeWorldMatrix(true).clone().invert();for(const leg of legs){const goal=leg.end.computeWorldMatrix(true).clone(),at=Vector3.TransformCoordinates(goal.getTranslation(),inverse);at.y=3/32*CREW_STUDY_SCALE+contact.footSupport-contact.lift;if(contact.footForward!==undefined)at.z=-contact.footForward;goal.setTranslation(Vector3.TransformCoordinates(at,actor.getWorldMatrix()));solveTwoBone(leg,goal);}kinematicsGoldens.push({kind:'seat',clip:'sit_idle',time:1.25,contact,bones:bones()});}scene.dispose();engine.dispose();
console.log(JSON.stringify({faceGoldens,kinematicsGoldens,footSteps:steps,seats,scale:CREW_STUDY_SCALE,equipment,definitions,hair:Object.fromEntries(['none','swept','cropped','crest','scientist','bob','ponytail','bun','braids'].map(k=>[k,[crewStudyHair(k,false),crewStudyHair(k,true)]])),defaults:Object.fromEntries(['engineer','marine','captain','medic','pilot','security','salvage','recon','scientist','mechanic','crew','explorer'].map(outfit=>[outfit,resolveCrewAppearance({outfit:outfit as any,equippedComponents:{}})])),uniforms:Object.fromEntries(CREW_WARDROBE.filter(i=>i.suit).map(i=>['wardrobe-'+i.id,i.suit])),armedClasses:Object.fromEntries(CREW_ITEMS.map(i=>[i.id,crewArmedClass(i)])),upperBones:[...VOXEL_CREW_UPPER_BONES,'hair.1','hair.2',...['L','R'].flatMap(side=>['thumb','index','fingers','prop'].map(b=>b+'.'+side))],clipFallback:VOXEL_CREW_CLIP_FALLBACK,nominalSpeeds:VOXEL_CREW_NOMINAL_SPEED,walkSpeed:WALK_SPEED_MPS,sprintSpeed:SPRINT_SPEED_MPS}));
'''
def sha(b):return hashlib.sha256(b).hexdigest()
def write_changed(p,b):
 if not p.exists() or p.read_bytes()!=b:p.write_bytes(b)
def raw_path(file):
 alias=DEST/(file+".bin");return alias if alias.exists() else DEST/file
def glb(p):
 b=p.read_bytes();assert b[:4]==b'glTF' and struct.unpack_from('<I',b,8)[0]==len(b)
 n=struct.unpack_from('<I',b,12)[0];return json.loads(b[20:20+n])
def animation_goldens(catalog):
 p=raw_path(catalog['animation']['file']);b=p.read_bytes();n=struct.unpack_from('<I',b,12)[0];doc=json.loads(b[20:20+n]);offset=28+n;joints=doc['skins'][0]['joints'];nodes=doc['nodes'];memo={}
 def values(index):
  if index in memo:return memo[index]
  a=doc['accessors'][index];view=doc['bufferViews'][a['bufferView']];components={'SCALAR':1,'VEC3':3,'VEC4':4}[a['type']];start=offset+view.get('byteOffset',0)+a.get('byteOffset',0);stride=view.get('byteStride',components*4)
  out=[list(struct.unpack_from('<'+'f'*components,b,start+i*stride)) for i in range(a['count'])];memo[index]=out;return out
 def slerp(a,b,t):
  dot=sum(x*y for x,y in zip(a,b))
  if dot<0:b=[-x for x in b];dot=-dot
  if dot>.999999:q=[x+(y-x)*t for x,y in zip(a,b)]
  else:
   angle=math.acos(max(-1,min(1,dot)));scale=math.sin(angle);q=[x*math.sin((1-t)*angle)/scale+y*math.sin(t*angle)/scale for x,y in zip(a,b)]
  norm=math.sqrt(sum(x*x for x in q));return [x/norm for x in q]
 animations={a['name']:a for a in doc['animations']};cases=[]
 for name,time,loop in [('idle',.137,True),('walk',.317,True),('run',.203,True),('sit_idle',1.25,True),('pistol.aim',.371,True),('pistol.shoot',.188,False),('pistol.reload',.375,False),('ZeroG_Prone',1.71,True),('ZeroG_Flight',.457,True),('Maglock_Walk',.177,True),('death',1.73,False)]:
  animation=animations[name];duration=max(values(s['input'])[-1][0] for s in animation['samplers']);at=max(0,time)%duration if loop and duration else min(duration,max(0,time));pose={nodes[j]['name']:{'position':nodes[j].get('translation',[0,0,0]),'rotation':nodes[j].get('rotation',[0,0,0,1]),'scale':nodes[j].get('scale',[1,1,1])} for j in joints}
  for channel in animation['channels']:
   sampler=animation['samplers'][channel['sampler']];ts=[v[0] for v in values(sampler['input'])];vs=values(sampler['output']);i=max(0,min(len(ts)-1,bisect.bisect_right(ts,at)-1));j=min(i+1,len(ts)-1);t=0 if i==j else max(0,min(1,(at-ts[i])/(ts[j]-ts[i])));path=channel['target']['path'];value=slerp(vs[i],vs[j],t) if path=='rotation' else [x+(y-x)*t for x,y in zip(vs[i],vs[j])];pose[nodes[channel['target']['node']]['name']][{'translation':'position','rotation':'rotation','scale':'scale'}[path]]=value
  cases.append({'clip':name,'time':time,'loop':loop,'duration':duration,'bones':pose})
 return {'schema':'sidereal.native-crew-pose-goldens.v1','sourceRevision':REVISION,'sourceAnimationSha256':catalog['animation']['sha256'],'method':'Independent Python glTF 2.0 LINEAR translation/scale interpolation and shortest-path normalized quaternion slerp from unchanged raw samplers. Absolute local bone poses; no root scaling or renderer conversion.','cases':cases}
def verify():
 m=json.loads((DEST/'manifest.json').read_text());c=json.loads((DEST/'catalog.json').read_text());s=json.loads((DEST/'semantics.json').read_text())
 assert m['sourceRevision']==REVISION and c['revision']=='study-v2-r001' and s['scale']==.9
 total=0
 for a in m['assets']:
  p=(DEST/a['file']).resolve();assert p.is_relative_to(DEST.resolve());b=p.read_bytes();assert len(b)==a['bytes'] and sha(b)==a['sha256'],a['file'];total+=len(b)
 for f in [c['body'],c['animation']]+[f for p in list(c['parts'].values())+list(c['items'].values()) for f in p['files'].values()]:
  b=raw_path(f['file']).read_bytes();assert len(b)==f['bytes'] and sha(b)==f['sha256'],f['file']
 body=glb(DEST/c['body']['file']);anim=glb(raw_path(c['animation']['file']))
 bones=[body['nodes'][i]['name'] for i in body['skins'][0]['joints']]
 assert len(bones)==32 and bones==[anim['nodes'][i]['name'] for i in anim['skins'][0]['joints']]
 assert len(anim['animations'])==len(c['clips'])==244 and all(len(a['channels'])==96 and all(t.get('interpolation','LINEAR')=='LINEAR' for t in a['samplers']) for a in anim['animations'])
 assert set(c['clips'])=={a['name'] for a in anim['animations']}
 rest=lambda n:[n.get('translation',[0,0,0]),n.get('rotation',[0,0,0,1]),n.get('scale',[1,1,1])]
 bnodes={n['name']:n for n in body['nodes'] if n.get('name') in bones}
 for f in {f['file'] for p in c['parts'].values() for f in p['files'].values()}:
  doc=glb(DEST/f);joints=doc['skins'][0]['joints'];assert [doc['nodes'][i]['name'] for i in joints]==bones,f
  for i in joints:assert rest(doc['nodes'][i])==rest(bnodes[doc['nodes'][i]['name']]),f
 assert len(c['parts'])==114 and len(c['items'])==38 and len(c['face'])==6
 print(f'Verified original study-v2-r001: {len(m["assets"])} files, {total:,} bytes, 32 matching rest joints, 244 complete clips, 114 wardrobe parts, 38 held items, six face atlases, one 0.90 scale')
def generate(repo):
 with tempfile.TemporaryDirectory(prefix='sidereal-native-crew-') as t:
  src=pathlib.Path(t);paths=['packages/content','packages/render','packages/sim','assets/runtime/crew/study-v2-r001','assets/runtime/ship-access','assets/runtime/wayfarer-access','tsconfig.json','package.json']
  proc=subprocess.Popen(['git','archive',REVISION,*paths],cwd=repo,stdout=subprocess.PIPE)
  with tarfile.open(fileobj=proc.stdout,mode='r|') as tar:tar.extractall(src,filter='data')
  assert proc.wait()==0
  deps=(repo/'node_modules').resolve();(src/'node_modules').symlink_to(deps,target_is_directory=True)
  (src/'export.ts').write_text(EXPORT);(src/'tsconfig-export.json').write_text(json.dumps({'compilerOptions':{'baseUrl':str(src),'paths':{'@sidereal/content/*':['packages/content/src/*'],'@sidereal/sim/*':['packages/sim/src/*']}}}))
  semantics=subprocess.check_output([str(deps/'.bin/tsx'),'--tsconfig',str(src/'tsconfig-export.json'),str(src/'export.ts')],cwd=src,env=dict(os.environ,SIDEREAL_CREW_WORLD_MANIFEST=str(ROOT/'Assets/World/manifest.json')))
  DEST.mkdir(parents=True,exist_ok=True);write_changed(DEST/'semantics.json',semantics)
  write_changed(DEST/'catalog.json',(src/'packages/content/src/crew-study.catalog.json').read_bytes())
  assets=[];base=src/'assets/runtime/crew/study-v2-r001'
  for p in sorted(base.rglob('*')):
   if not p.is_file():continue
   rel=p.relative_to(base).as_posix();b=p.read_bytes();source_rel=rel;rel=rel+'.bin' if rel=='anim/crew-anims.glb' else rel;out=DEST/rel;out.parent.mkdir(parents=True,exist_ok=True);write_changed(out,b);assets.append({'file':rel,'sourceFile':source_rel,'sha256':sha(b),'bytes':len(b)})
  for variant,face in json.loads((DEST/'catalog.json').read_text())['face'].items():
   rel=face['png'];b=(DEST/rel).read_bytes();write_changed(DEST/(rel+'.bin'),b);assets.append({'file':rel+'.bin','sourceFile':rel,'sha256':sha(b),'bytes':len(b),'adaptation':'byte-identical raw PNG; opaque extension survives exported resource remapping'})
  obsolete=DEST/'anim/crew-anims.glb'
  if obsolete.exists():obsolete.unlink()
  write_changed(DEST/'golden.json',(json.dumps(animation_goldens(json.loads((DEST/'catalog.json').read_text())),separators=(',',':'))+'\n').encode())
  for rel in ['catalog.json','semantics.json','golden.json']:
   b=(DEST/rel).read_bytes();assets.append({'file':rel,'sha256':sha(b),'bytes':len(b)})
  source_files=['packages/content/src/crew-study.ts','packages/content/src/crew-study.catalog.json','packages/content/src/crew-items.ts','packages/content/src/inventory.ts','packages/render/src/crew/appearance.ts','packages/render/src/crew/voxel-crew.ts','packages/render/src/crew/crew-study-outfit.ts','packages/render/src/crew/voxel-crew-regions.ts','packages/render/src/crew/crew-study-face.ts','apps/client/src/crewmates.ts','packages/render/src/crew/remote-crew-motion.ts','packages/render/src/crew/voxel-crew-clips.ts','packages/render/src/crew/voxel-ik.ts','packages/render/src/crew/seat-presentation.ts']
  # crewmates is separately read from the exact commit, never a mutable working file.
  sources=[]
  for file in source_files:
   b=(src/file).read_bytes() if (src/file).exists() else subprocess.check_output(['git','show',REVISION+':'+file],cwd=repo);sources.append({'file':file,'sha256':sha(b)})
  manifest={'schema':'sidereal.native-crew-assets.v1','sourceRepository':'https://github.com/Dastari/sidereal_spacetime','sourceRevision':REVISION,'revision':'study-v2-r001','scope':'Exact existing browser provisional runtime assets; this import grants no new art approval and changes no Opus source asset. Original mesh scale and source skin rest transforms are preserved. Runtime applies the single browser 0.90 root scale.','assets':assets,'sources':sources}
  write_changed(DEST/'manifest.json',(json.dumps(manifest,indent=2)+'\n').encode())
 verify()
if __name__=='__main__':
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('--source-repo',type=pathlib.Path);p.add_argument('--verify',action='store_true');a=p.parse_args()
 if a.verify:verify()
 elif a.source_repo:generate(a.source_repo.resolve())
 else:p.error('Pass --source-repo or --verify')
