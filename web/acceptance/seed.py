import base64,json,struct,zlib,urllib.request,uuid
from pathlib import Path
# Seeds only the dedicated isolated Issue 27 receiver on port 18142.
def png(rgb):
 def chunk(kind,data):return struct.pack('>I',len(data))+kind+data+struct.pack('>I',zlib.crc32(kind+data)&0xffffffff)
 return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',32,32,8,2,0,0,0))+chunk(b'IDAT',zlib.compress((b'\x00'+bytes(rgb)*32)*32))+chunk(b'IEND',b'')
model={'version':1,'vertices':[0,0,0,1,0,0,0,1,0],'colors':[255,0,0,0,255,0,0,0,255],'triangles':[0,1,2],'marker':{'position':{'x':.25,'y':.25,'z':0},'normal':{'x':0,'y':0,'z':1}}}
body={'reportVersion':2,'title':'永久删除验收','description':'同一 ID 的旧资料','priority':'high','position':{'x':1,'y':2,'z':3},'snapshot':base64.b64encode(png((230,50,30))).decode(),'model':model}

for index,(title,description,priority,status) in enumerate([('阀门漏水','北区水管检查','high','open'),('照明故障','南区灯泡需更换','medium','in_progress'),('护栏修复','北区围栏已完成','low','resolved')]):
 body.update(title=title,description=description,priority=priority)
 if index==3: body.pop('model');body.pop('snapshot');body.pop('reportVersion')
 req=urllib.request.Request('http://127.0.0.1:18142/api/reports',json.dumps(body).encode(),{'Content-Type':'application/json','Idempotency-Key':f'issue27-{uuid.uuid4()}'})
 with urllib.request.urlopen(req) as r: issue=json.load(r)['data']
 if status!='open':
  req=urllib.request.Request('http://127.0.0.1:18142/api/issues/'+issue['id']+'/status',json.dumps({'status':status}).encode(),{'Content-Type':'application/json'},method='PATCH')
  urllib.request.urlopen(req).close()
 print(title,issue['id'])
