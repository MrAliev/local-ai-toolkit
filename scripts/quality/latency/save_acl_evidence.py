"""Validate four complete MCP benchmark runs and retain compact per-query evidence."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('input', type=Path)
parser.add_argument('output', type=Path)
args = parser.parse_args()
runs = []
expected = {}
identity = None
variants = {}
for name in ('baseline-1', 'candidate-1', 'candidate-2', 'baseline-2'):
    path = args.input / (name + '.json')
    report = json.loads(path.read_text(encoding='utf-8'))
    if not report['complete'] or report['run'] != name or len(report['observations']) != 68:
        raise ValueError('Incomplete run: ' + name)
    current = {k: report[k] for k in ('casesSha256', 'corpusCommit', 'policy', 'brokerIdentity', 'probeSha256')}
    before = re.search(r'^Index:\s+(.+)$', report['indexBefore'], re.M).group(1).strip()
    after = re.search(r'^Index:\s+(.+)$', report['indexAfter'], re.M).group(1).strip()
    if before != after:
        raise ValueError('Index changed')
    current['indexPath'] = before
    model = re.search(r'^Model:\s+(\S+)', report['indexBefore'], re.M).group(1)
    current['model'] = model
    if identity is not None and current != identity:
        raise ValueError('Run identities changed')
    identity = current
    variant = name.split('-')[0]
    if variant in variants and variants[variant] != report['binaryHashes']:
        raise ValueError('Variant binaries changed')
    variants[variant] = report['binaryHashes']
    rows = []
    sequence = [(r['case'], r['repeat']) for r in report['observations']]
    cases = [case for case, repeat in sequence[:34]]
    if len(set(cases)) != 34 or sequence != [(case, repeat) for repeat in range(2) for case in cases]:
        raise ValueError('Incomplete/reordered corpus')
    for row in report['observations']:
        if [entry['model'] for entry in row['models']['actual']] != [model]:
            raise ValueError('Missing or unexpected embedding model')
        if not math.isfinite(row['elapsedMs']) or row['elapsedMs'] < 0:
            raise ValueError('Invalid timer')
        hit_hash = row['displayedHitSha256']
        if row['case'] in expected and expected[row['case']] != hit_hash:
            raise ValueError('Retrieval changed: ' + row['case'])
        expected[row['case']] = hit_hash
        compact = {k: row[k] for k in ('case', 'repeat', 'elapsedMs', 'displayedHitSha256', 'hitCount')}
        compact['models'] = row['models']['actual']
        for metric in ('queue_milliseconds', 'execution_milliseconds'):
            compact[metric] = row['timings'][metric]
        rows.append(compact)
    quantiles = []
    for repeat in range(2):
        values = sorted(r['elapsedMs'] for r in rows if r['repeat'] == repeat)
        quantiles.append({'repeat': repeat, 'n': len(values), 'p50Ms': values[math.ceil(.5*len(values))-1],
                          'p90Ms': values[math.ceil(.9*len(values))-1]})
    runs.append({'run': name, 'rawSha256': hashlib.sha256(path.read_bytes()).hexdigest(),
                 'quantiles': quantiles, 'observations': rows})
    print(name, json.dumps(quantiles))
code_identity = json.loads((args.input / 'code-identity.json').read_text(encoding='utf-8'))
if {item['library'] for item in code_identity} != {'codesearch-mcp.dll', 'CodeSearch.Core.dll', 'LocalAi.Broker.Client.dll'} or len(code_identity) != 3:
    raise ValueError('Incomplete code identity check')
for item in code_identity:
    if item['baseline']['CodeSha256'] != item['candidate']['CodeSha256'] or item['baseline']['MethodCount'] <= 0:
        raise ValueError('Non-ACL method bodies changed')
    for variant in ('baseline', 'candidate'):
        if item[variant]['FileSha256'] != variants[variant][item['library']]:
            raise ValueError('Code identity refers to different binaries')
if variants['baseline']['LocalAi.Broker.Core.dll'] == variants['candidate']['LocalAi.Broker.Core.dll']:
    raise ValueError('No ACL implementation difference')
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps({'schemaVersion': 1, 'identity': identity, 'variants': variants, 'codeIdentity': code_identity,
                                   'matchedRetrievals': 272, 'runs': runs}, indent=2) + '\n', encoding='utf-8')
