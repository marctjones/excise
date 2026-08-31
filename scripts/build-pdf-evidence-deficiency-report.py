#!/usr/bin/env python3
"""Generate a mode-by-mode evidence deficiency report and prioritized review queue."""
from __future__ import annotations
import json, re
from collections import Counter, defaultdict
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
REG=ROOT/'test-pdfs/manifests/pdf-spec-registry'
OUT=REG/'generated/evidence-deficiency-report.json'

def load(path): return json.loads(path.read_text(encoding='utf-8'))
def linked(records, capability, mode):
 return [row['id'] for row in records if any(link['capability']==capability and mode in link['modes'] for link in row['parentCapabilityModes'])]
def check_method(ref):
 return ref.rsplit('::', 1)[-1]
def outcome_method(name):
 match=re.search(r'\.([A-Za-z_][A-Za-z0-9_]*)\(', name)
 return match.group(1) if match else name.rsplit('.', 1)[-1].split('(', 1)[0]
def execution_status(checks, outcomes):
 """Report recorded execution separately from static evidence eligibility."""
 if not checks: return 'not-contracted', []
 by_method=defaultdict(list)
 for outcome in outcomes:
  name=outcome.get('testName') or ''
  by_method[outcome_method(name)].append(outcome)
 recorded=[]
 for check in checks:
  matches=by_method.get(check_method(check['ref']), [])
  recorded.append({'kind':check['kind'],'ref':check['ref'],'outcomes':sorted({item['outcome'] for item in matches})})
 seen=[item for item in recorded if item['outcomes']]
 if not seen: return 'not-recorded', recorded
 if any('Failed' in item['outcomes'] for item in seen): return 'failed', recorded
 if len(seen)!=len(recorded): return 'partial', recorded
 if all(item['outcomes']==['Passed'] for item in seen): return 'passed', recorded
 return 'mixed', recorded
def main():
 registry=load(REG/'registry.json')
 sources=load(REG/'generated/implementation-evidence-map.json')['sources']
 tests=load(REG/'generated/test-suite-evidence-map.json')['tests']
 renderer=load(REG/'generated/renderer-test-evidence-map.json')['tests']
 outcomes=load(REG/'generated/test-outcomes.json').get('outcomes', [])
 discovery={row['id']:row for row in load(REG/'generated/evidence-collection.json')['capabilities']}
 workflow_ids={
  'redaction':{'pdf.17.security.redaction-content-removal','pdf.17.interactive.redaction-annotations'},
  'forms':{'pdf.17.interactive.forms'},
  'safe-save':{'pdf.17.syntax.objects','pdf.17.document.metadata','pdfe.product.security.privacy-clean-copy'},
  'rendering':{'pdf.17.content.streams','pdf.17.graphics.images','pdf.17.graphics.fonts','pdf.17.transparency.model'},
 }
 rows=[]
 for section in registry['sections']:
  for cap in load(REG/section['path'])['capabilities']:
   if cap['decision']['state'] not in {'required','supported'}: continue
   verification=cap.get('verification',{})
   checks=verification.get('checks',[]) if verification.get('status')=='executable' else []
   for mode,state in cap['modes'].items():
    if state=='not-applicable': continue
    source=linked(sources,cap['id'],mode)
    direct=linked(tests,cap['id'],mode)
    renderer_direct=linked(renderer,cap['id'],mode)
    candidates=discovery.get(cap['id'], {}).get('candidateReferences', [])
    candidate_source=[row for row in candidates if row['kind']=='source']
    candidate_tests=[row for row in candidates if row['kind']=='test']
    candidate_docs=[row for row in candidates if row['kind']=='architecture']
    atomic=[check['ref'] for check in checks if check['kind']=='atomic-fixture' and mode in check['modes']]
    independent=[check['ref'] for check in checks if check['kind'] in {'differential','corpus'} and mode in check['modes']]
    mode_checks=[check for check in checks if mode in check['modes']]
    outcome_status, recorded_checks=execution_status(mode_checks, outcomes)
    deficiencies=[]
    if state!='implemented':
     if not source: deficiencies.append('no-mapped-implementation-candidate')
     if not direct: deficiencies.append('no-mapped-direct-test-candidate')
     if not atomic: deficiencies.append('no-accepted-atomic-fixture')
     if not independent: deficiencies.append('no-accepted-independent-or-corpus-evidence')
     if mode not in verification.get('requiredModes',[]): deficiencies.append('no-executable-mode-verification-contract')
    workflow=next((name for name,ids in workflow_ids.items() if cap['id'] in ids),'other')
    priority=0
    if workflow=='redaction': priority+=100
    if workflow in {'rendering','safe-save','forms'}: priority+=50
    if cap['classification']=='core': priority+=20
    if cap['decision']['state']=='required': priority+=10
    priority+=len(deficiencies)
    mapped_tests=renderer_direct or direct
    discovery_state=('mapped-source-and-test' if source and mapped_tests else 'mapped-source-only' if source else 'mapped-test-only' if mapped_tests else 'source-and-test-candidates' if candidate_source and candidate_tests else 'source-candidates-only' if candidate_source else 'test-candidates-only' if candidate_tests else 'no-repository-candidate')
    next_action=('none; strict implementation is recorded' if state=='implemented' else 'review mapped code and test candidates' if source and (renderer_direct or direct) else 'review deterministic discovery candidates and accept/reject them' if candidate_source or candidate_tests else 'find or write a focused implementation/test pair')
    rows.append({'capability':cap['id'],'section':section['id'],'workflow':workflow,'mode':mode,'currentState':state,'deficiencies':deficiencies,'discoveryStatus':discovery_state,'discoveryCandidates':{'source':candidate_source[:40],'test':candidate_tests[:80],'architecture':candidate_docs[:20]},'executionStatus':outcome_status,'recordedChecks':recorded_checks,'priority':priority,'implementationCandidates':source[:40],'directTestCandidates':(renderer_direct or direct)[:80],'atomicFixtures':atomic,'independentEvidence':independent,'nextAction':next_action})
 rows.sort(key=lambda row:(-row['priority'],row['capability'],row['mode']))
 by_deficiency=Counter(item for row in rows for item in row['deficiencies'])
 by_execution=Counter(row['executionStatus'] for row in rows)
 by_discovery=Counter(row['discoveryStatus'] for row in rows)
 by_workflow=defaultdict(Counter)
 for row in rows: by_workflow[row['workflow']][row['currentState']]+=1
 result={
  'schemaVersion':1,
  'generatedBy':'scripts/build-pdf-evidence-deficiency-report.py',
  'policy':'A missing map candidate is a traceability deficiency, not proof that code does not exist. A listed candidate is not proof of support until a reviewer accepts it into the capability verification contract. Execution status reports imported TRX results only; it is separate from static evidence eligibility.',
  'modes':rows,
  'summary':{
   'targetModes':len(rows),
   'strictImplemented':sum(row['currentState']=='implemented' for row in rows),
   'modesWithDeficiencies':sum(bool(row['deficiencies']) for row in rows),
   'deficiencies':dict(sorted(by_deficiency.items())),
   'executionStatus':dict(sorted(by_execution.items())),
   'discoveryStatus':dict(sorted(by_discovery.items())),
   'byWorkflow':{name:dict(sorted(counts.items())) for name,counts in sorted(by_workflow.items())},
   'topPriority':[{key:row[key] for key in ('capability','mode','workflow','currentState','deficiencies','priority','nextAction')} for row in rows[:50]]
  }
 }
 OUT.write_text(json.dumps(result,indent=2)+'\n')
 print(f"wrote {OUT} with {len(rows)} target-mode deficiencies")
if __name__=='__main__': main()
