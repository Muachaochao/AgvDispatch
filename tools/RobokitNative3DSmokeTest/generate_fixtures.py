"""Independent Google Protobuf encoder using the supplied schema's exact 3D dependency graph."""
from pathlib import Path
import re, json, hashlib, zipfile, io
from google.protobuf import descriptor_pb2, descriptor_pool, message_factory
ROOT=Path(__file__).resolve().parents[2]
source=ROOT/'docs/vendor/robokit/map3d-definition-20261015/user-supplied-map.proto'
text=source.read_text(encoding='utf-8-sig')
names=['Message_MapPos','Message_MapHeader','Vec3f','Vec3i','FeatureMapParams','FeatureMapPlane','OctoTree','OctoTrees','FeatureMap3D','Message_Map3D']
f=descriptor_pb2.FileDescriptorProto(name='map3d_supplied_subset.proto',package='rbk.protocol',syntax='proto3')
scalar={'double':1,'float':2,'int32':5,'uint32':13,'string':9}
for name in names:
 start=re.search(r'\bmessage\s+'+re.escape(name)+r'\s*\{',text).end();level=1;end=start
 while level:
  if text[end]=='{':level+=1
  elif text[end]=='}':level-=1
  end+=1
 body=re.sub(r'//[^\n]*','',text[start:end-1]);message=f.message_type.add(name=name)
 for repeated,kind,key,number in re.findall(r'(?m)^\s*(repeated\s+)?(\w+)\s+(\w+)\s*=\s*(\d+)\s*;',body):
  field=message.field.add(name=key,number=int(number),label=3 if repeated else 1)
  if kind in scalar:field.type=scalar[kind]
  else:
   assert kind in names,(name,kind)
   field.type=11;field.type_name='.rbk.protocol.'+kind
pool=descriptor_pool.DescriptorPool();pool.Add(f)
Map=message_factory.GetMessageClass(pool.FindMessageTypeByName('rbk.protocol.Message_Map3D'))
msg=Map();msg.map_directory='synthetic/no-device'
msg.header.map_type='3D-Map';msg.header.map_name='NATIVE-3D-SYNTHETIC';msg.header.version='1.0.6';msg.header.resolution=.02
msg.header.min_pos.x=-22.211;msg.header.min_pos.y=-1.377;msg.header.min_pos.z=-.1
msg.header.max_pos.x=4.179;msg.header.max_pos.y=14.657;msg.header.max_pos.z=3.6
xyz=ROOT/'docs/examples/pointcloud-3d/synthetic-workshop.xyz'
for line in xyz.read_text().splitlines():
 if line.startswith('#') or not line.strip():continue
 x,y,z=map(float,line.split());p=msg.normal_pos3d_list.add();p.x=x-11;p.y=y+6;p.z=z
plane=msg.feature_map_3d.planes.add();plane.center.x=-11;plane.center.y=6;plane.normal.z=1;plane.radius=1
out=ROOT/'docs/examples/robokit-native3d';out.mkdir(parents=True,exist_ok=True)
raw=msg.SerializeToString(deterministic=True);(out/'synthetic-native.3dsmap').write_bytes(raw)
sets=descriptor_pb2.FileDescriptorSet();sets.file.add().CopyFrom(f);(out/'supplied-map3d-subset.desc').write_bytes(sets.SerializeToString())
plane_path=ROOT/'docs/vendor/robokit/map-format-20261014/smap-master/maps/6.smap'
plane_json=json.loads(plane_path.read_text());plane_json['header']['mapName']=msg.header.map_name
planebytes=json.dumps(plane_json,separators=(',',':'),ensure_ascii=False).encode()
with zipfile.ZipFile(out/'synthetic-native-3d.smap','w',compression=zipfile.ZIP_DEFLATED) as z:
 for name,data in [('info.json',b'{"zipName":"SYNTHETIC-NATIVE-NOT-DEVICE"}'),('0.smap',planebytes),('0.3dsmap',raw)]:
  info=zipfile.ZipInfo(name,date_time=(2026,10,15,0,0,0));info.compress_type=zipfile.ZIP_DEFLATED;z.writestr(info,data)
def save(name,change):
 m=Map();m.CopyFrom(msg);change(m);(out/name).write_bytes(m.SerializeToString(deterministic=True))
save('feature-only.3dsmap',lambda m:m.ClearField('normal_pos3d_list'))
save('wrong-type.3dsmap',lambda m:setattr(m.header,'map_type','2D-Map'))
save('nan-point.3dsmap',lambda m:setattr(m.normal_pos3d_list[0],'x',float('nan')))
save('out-of-bounds.3dsmap',lambda m:setattr(m.normal_pos3d_list[0],'z',100))
save('bad-bounds.3dsmap',lambda m:setattr(m.header.min_pos,'x',100))
save('missing-header.3dsmap',lambda m:m.ClearField('header'))
save('bad-resolution.3dsmap',lambda m:setattr(m.header,'resolution',0))
save('unknown-version.3dsmap',lambda m:setattr(m.header,'version','vendor-version-not-assumed'))
save('different-name.3dsmap',lambda m:setattr(m.header,'map_name','DIFFERENT'))
def zero(m):
 m.ClearField('normal_pos3d_list');m.normal_pos3d_list.add()
save('zero-default-point.3dsmap',zero)
manifest={'source':'User supplied complete Message_Map3D dependency graph; independent Google Protobuf encoder','schemaSha256':hashlib.sha256(source.read_bytes()).hexdigest().upper(),'synthetic':True,'deviceSample':False,'pointCount':len(msg.normal_pos3d_list),'ordinaryFieldNumber':3,'messages':names,'planeOriginalSource':str(plane_path.relative_to(ROOT)),'planeOriginalSha256':hashlib.sha256(plane_path.read_bytes()).hexdigest().upper(),'planeChange':'mapName replaced in copied JSON only; original untouched','files':{p.name:hashlib.sha256(p.read_bytes()).hexdigest().upper() for p in sorted(out.iterdir()) if p.suffix in ('.smap','.3dsmap','.desc')}}
(out/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print('Generated native Protobuf fixtures:',len(msg.normal_pos3d_list),'points; not real device data')
