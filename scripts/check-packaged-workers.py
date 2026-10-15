import argparse, base64, hashlib, json, math, os, pathlib, queue, struct, subprocess, threading, time, wave

parser=argparse.ArgumentParser(description='Verify actual packaged CPU workers with pinned local model files.')
parser.add_argument('--package',required=True)
parser.add_argument('--models',required=True)
parser.add_argument('--model',default='all',choices=['all','bge-m3','nomic','gemma2'])
parser.add_argument('--report-dir',default='state/packaged-workers')
args=parser.parse_args()
repo=pathlib.Path(__file__).resolve().parent.parent
package=pathlib.Path(args.package).resolve()
if not (package/'wai-embed.exe').is_file():raise SystemExit('Missing packaged wai-embed.exe')
root=pathlib.Path(args.report_dir).resolve()
models=pathlib.Path(args.models).resolve()
if root.is_relative_to(package) or root.is_relative_to(models):raise SystemExit('Reports must be outside package and model directories')
root.mkdir(parents=True,exist_ok=True)
subprocess.run([str(package/'ffmpeg.exe'),'-hide_banner','-loglevel','error','-y','-i',str((repo/'checks/WAI.ReleaseSmoke/Fixtures/av1.webm')),'-frames:v','1',str(root/'image.png')],check=True,timeout=60)
with wave.open(str(root/'audio.wav'),'wb') as stream:
    stream.setnchannels(1);stream.setsampwidth(2);stream.setframerate(16000)
    stream.writeframes(b''.join(struct.pack('<h',int(8000*math.sin(2*math.pi*440*i/16000))) for i in range(16000)))
results=[]
for model,dim in [('bge-m3',1024),('nomic',768),('gemma2',768)]:
    if args.model!='all' and args.model!=model:continue
    env=dict(os.environ,WAI_MODELS=str(models),WAI_BACKEND='cpu',WAI_HOME=str(root/model/'home'),WAI_INDEX=str(root/model/'index'),WAI_FFMPEG=str(package))
    stderr=(root/(model+'-stderr.log')).open('w',encoding='utf-8')
    worker_args=[str(package/'wai-embed.exe'),'--model',model,'--cpu','--threads','4']
    if model!='gemma2':worker_args.append('--text-only')
    process=subprocess.Popen(worker_args,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=stderr,text=True,encoding='utf-8',env=env)
    output=queue.Queue()
    def read_stdout():
        for line in process.stdout:output.put(line)
        output.put(None)
    threading.Thread(target=read_stdout,daemon=True).start()
    def receive():
        line=output.get(timeout=180)
        if line is None:raise RuntimeError('Worker exited before response: '+model)
        return json.loads(line)
    def request(body):
        process.stdin.write(json.dumps(body,ensure_ascii=False)+'\n');process.stdin.flush()
        reply=receive();assert reply.get('id')==body['id'] and reply.get('ok'),reply
        return reply
    try:
        started=time.monotonic();ready=receive();assert ready.get('ready'),ready
        info=request({'id':1,'op':'info'})['info']
        assert info['dim']==dim and info['backend'].lower()=='cpu',info
        if model=='gemma2':assert info['text_only'] is False,info
        checks=[]
        operations=[('text','Search for documents about data backups.')]
        if model=='gemma2':operations += [('image',str(root/'image.png')),('audio',str(root/'audio.wav'))]
        for index,(op,value) in enumerate(operations,start=2):
            before=time.monotonic();reply=request({'id':index,'op':op,'inputs':[value],'budget':70})
            assert reply['dim']==dim and reply['count']==1 and not any(reply['errors']),reply.get('errors')
            payload=base64.b64decode(reply['vectors'],validate=True);assert len(payload)==dim*4
            values=struct.unpack('<'+'f'*dim,payload)
            assert all(math.isfinite(x) for x in values)
            norm=math.sqrt(math.fsum(x*x for x in values));assert 0.9<norm<1.1,norm
            checks.append({'operation':op,'dimensions':dim,'norm':norm,'seconds':time.monotonic()-before})
            print('Passed packaged worker',model,op,dim,flush=True)
        request({'id':99,'op':'quit'});assert process.wait(timeout=30)==0
        results.append({'model':model,'status':'passed','textOnly':info['text_only'],'checks':checks,'seconds':time.monotonic()-started})
    finally:
        if process.poll() is None:process.kill();process.wait(timeout=30)
        stderr.close()
(root/'summary.json').write_text(json.dumps({'status':'passed','backend':'cpu','workerExecutableSha256':hashlib.file_digest((package/'wai-embed.exe').open('rb'),'sha256').hexdigest(),'workerAssemblySha256':hashlib.file_digest((package/'wai-embed.dll').open('rb'),'sha256').hexdigest(),'results':results,'limitation':'Synthetic input protocol/inference checks, not search quality or a GPU compatibility matrix.'},indent=2),encoding='utf-8')
print('Requested packaged worker checks passed.',flush=True)
