#!/usr/bin/env python3
"""Build a mode-level inventory of accepted atomic-fixture contracts and runs."""
from __future__ import annotations
import json
from collections import Counter, defaultdict
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
REG=ROOT/'test-pdfs/manifests/pdf-spec-registry'
OUT=REG/'generated/atomic-fixture-evidence.json'

def load(path): return json.loads(path.read_text(encoding='utf-8'))
def method(ref): return ref.rsplit('::',1)[-1]
def main():
 registry=load(REG/'registry.json')
 tests=load(REG/'generated/test-suite-evidence-map.json')['tests']
 outcomes=load(REG/'generated/test-outcomes.json').get('outcomes',[])
 test_index={(row['path'],row['method']):row['id'] for row in tests}
 outcome_index=defaultdict(set)
 for outcome in outcomes: outcome_index[(outcome.get('testName') or '').rsplit('.',1)[-1]].add(outcome['outcome'])
 rows=[]
 for section in registry['sections']:
  for cap in load(REG/section['path'])['capabilities']:
   if cap['decision']['state'] not in {'required','supported'}: continue
   checks=cap.get('verification',{}).get('checks',[])
   for mode,state in cap['modes'].items():
    if state=='not-applicable': continue
    fixtures=[]
    for check in checks:
     if check['kind']!='atomic-fixture' or mode not in check['modes']: continue
     if '::' not in check['ref']:
      fixtures.append({'ref':check['ref'],'testRecord':None,'recordedOutcomes':[],'resolution':'non-method-reference'})
      continue
     path,check_method=check['ref'].split('::',1)
     fixtures.append({'ref':check['ref'],'testRecord':test_index.get((path,check_method)),'recordedOutcomes':sorted(outcome_index[method(check['ref'])]),'resolution':'method-reference'})
    status='not-contracted' if not fixtures else 'contracted-unresolved-reference' if any(item['resolution']!='method-reference' or item['testRecord'] is None for item in fixtures) else 'recorded-passing' if all(item['recordedOutcomes']==['Passed'] for item in fixtures) else 'contracted-not-run' if all(not item['recordedOutcomes'] for item in fixtures) else 'recorded-incomplete-or-failing'
    rows.append({'capability':cap['id'],'section':section['id'],'mode':mode,'currentState':state,'fixtureStatus':status,'fixtures':fixtures})
 summary=Counter(row['fixtureStatus'] for row in rows)
 result={'schemaVersion':1,'generatedBy':'scripts/build-pdf-atomic-fixture-map.py','policy':'An atomic fixture is accepted only when explicitly named in a capability verification contract. A passing imported TRX result proves only that recorded run, not strict implementation.','modes':rows,'summary':{'targetModes':len(rows),'fixtureStatus':dict(sorted(summary.items()))}}
 OUT.write_text(json.dumps(result,indent=2)+'\n')
 print(f"wrote {OUT} with {summary['recorded-passing']} recorded passing atomic-fixture modes")
if __name__=='__main__': main()
