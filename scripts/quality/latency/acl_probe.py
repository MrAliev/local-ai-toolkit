import sys
sys.dont_write_bytecode = True
import argparse,hashlib,json,os,queue,subprocess,threading,time,math
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parent))
from retention_probe import Mcp
from save_retention_evidence import blocks

parser=argparse.ArgumentParser(description='Compare complete MCP calls with two built client directories through the installed shared broker.')
for argument in ('corpus','cases','baseline','candidate','output'): parser.add_argument(argument,type=Path)
args=parser.parse_args()
out=args.output.resolve()
corpus=args.corpus.resolve()
cases_path=args.cases.resolve()
assert not out.is_relative_to(corpus), 'Output must be outside corpus'
assert not subprocess.check_output(['git','-C',str(corpus),'status','--porcelain'],text=True).strip(), 'Dirty corpus'
commit=subprocess.check_output(['git','-C',str(corpus),'rev-parse','HEAD'],text=True).strip()
out.mkdir(parents=True,exist_ok=False)
cases=json.loads(cases_path.read_text(encoding='utf-8-sig'))['cases']
runtime=Path(os.environ['LOCALAPPDATA'])/'LocalAi'

class BinaryMcp(Mcp):
    def __init__(self,binary,name):
        self.errors=(out/(name+'.stderr.log')).open('w',encoding='utf-8')
        self.process=subprocess.Popen([str(binary)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,
            stderr=self.errors,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
        self.incoming=queue.Queue(); self.identifier=0
        threading.Thread(target=self.read,daemon=True).start()
        self.info=self.request('initialize',{'protocolVersion':'2024-11-05','capabilities':{},
            'clientInfo':{'name':'acl-latency-probe','version':'1'}})
        self.send({'jsonrpc':'2.0','method':'notifications/initialized'})

def hash_file(path): return hashlib.sha256(path.read_bytes()).hexdigest()
policy=(runtime/'settings/policy.json').read_text()
host=json.loads((runtime/'host.json').read_text())
identity={key:host[key] for key in ('ProcessId','StartedAtUtc','BrokerAssemblyPath')}
expected={}
for name in ('baseline-1','candidate-1','candidate-2','baseline-2'):
    directory=(args.baseline if name.startswith('baseline') else args.candidate).resolve()
    client=BinaryMcp(directory/'codesearch-mcp.exe',name)
    rows=[]
    record={'run':name,'probeSha256':hash_file(Path(__file__)),
        'casesSha256':hash_file(cases_path),'corpusCommit':commit,'policy':json.loads(policy),'brokerIdentity':identity,
        'binaryHashes':{file:hash_file(directory/file) for file in ('codesearch-mcp.dll','CodeSearch.Core.dll','LocalAi.Broker.Core.dll','LocalAi.Broker.Client.dll')},
        'observations':rows,'complete':False}
    try:
        status=client.call('index_status',{'root':str(corpus)})['result']
        status_text='\n'.join(c.get('text','') for c in status['content'])
        assert 'Status:     current' in status_text, status_text
        record['indexBefore']=status_text.split('Repository:',1)[-1]
        for repeat in range(2):
            for case in cases:
                call=client.call('search_code',{'root':str(corpus),'query':case['query'],
                    'topK':10,'maxPerFile':3,'sourceContext':'AlreadyPresent'})
                result=call['result']
                usage=result['_meta']['localai/usage']
                assert not result.get('isError') and usage['outcome']['technical']=='succeeded',result
                hits=[(origin,body) for _,origin,body in blocks(result)]
                # Empty answers are permitted only with an explicit successful no-match response.
                assert hits or any('No matches' in c.get('text','') or 'Совпадений нет' in c.get('text','') for c in result['content']), result
                digest=hashlib.sha256(json.dumps(hits,ensure_ascii=False).encode()).hexdigest()
                if case['id'] in expected: assert expected[case['id']]==digest,(name,case['id'],'changed hits')
                else: expected[case['id']]=digest
                notice=next((c['text'] for c in result['content'] if c['type']=='text' and 'INPUT' in c['text']),None)
                rows.append({'case':case['id'],'repeat':repeat,'elapsedMs':call['elapsedMs'],'displayedHitSha256':digest,
                    'hitCount':len(hits),'models':usage['models'],'timings':usage['timings'],'usageNotice':notice})
                (out/(name+'.json')).write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8')
                print(json.dumps({'run':name,'pass':repeat,'done':len(rows),'of':len(cases)*2,'ms':call['elapsedMs']}),flush=True)
        assert (runtime/'settings/policy.json').read_text()==policy
        current=json.loads((runtime/'host.json').read_text())
        assert all(current[k]==v for k,v in identity.items())
        assert not subprocess.check_output(['git','-C',str(corpus),'status','--porcelain'],text=True).strip()
        assert subprocess.check_output(['git','-C',str(corpus),'rev-parse','HEAD'],text=True).strip()==commit
        status=client.call('index_status',{'root':str(corpus)})['result']
        status_text='\n'.join(c.get('text','') for c in status['content'])
        assert 'Status:     current' in status_text, status_text
        record['indexAfter']=status_text.split('Repository:',1)[-1]
        record['complete']=True
        (out/(name+'.json')).write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8')
    finally: client.close()
for name in ('baseline-1','candidate-1','candidate-2','baseline-2'):
    report=json.loads((out/(name+'.json')).read_text(encoding='utf-8'))
    for repeat in range(2):
        values=sorted(r['elapsedMs'] for r in report['observations'] if r['repeat']==repeat)
        print(name,repeat,'p50',values[math.ceil(.5*len(values))-1],'p90',values[math.ceil(.9*len(values))-1],flush=True)
