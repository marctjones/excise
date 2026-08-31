#!/usr/bin/env python3
"""Attribute discovered tests and benchmarks to registry modes without granting support credit."""
from __future__ import annotations
import json, re
from collections import Counter, defaultdict
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]; REG=ROOT/'test-pdfs/manifests/pdf-spec-registry'; OUT=REG/'generated/evidence-attribution.json'
def load(p): return json.loads(p.read_text())
def method(name):
 name=name.rsplit('::',1)[-1]
 m=re.search(r'\.([A-Za-z_][A-Za-z0-9_]*)\(',name)
 return m.group(1) if m else name.rsplit('.',1)[-1]
def main():
 registry=load(REG/'registry.json'); tests=load(REG/'generated/test-suite-evidence-map.json')['tests']; renderer=load(REG/'generated/renderer-test-evidence-map.json')['tests']; outcomes=load(REG/'generated/test-outcomes.json')['outcomes']; benchmarks=load(REG/'benchmarks.json')['scenarios']
 all_tests={row['id']:row for row in tests}; all_tests.update({row['id']:row for row in renderer})
 outcome_methods=defaultdict(set)
 for row in outcomes: outcome_methods[method(row['testName'])].add(row['outcome'])
 candidates=defaultdict(list)
 for test in all_tests.values():
  for link in test['parentCapabilityModes']:
   for mode in link['modes']:
    candidates[(link['capability'],mode)].append({'test':test['id'],'mappingSources':link.get('mappingSources',['keyword-facet-candidate']),'recordedOutcomes':sorted(outcome_methods[test['method']])})
 contracts=defaultdict(list); rows=[]
 for section in registry['sections']:
  for cap in load(REG/section['path'])['capabilities']:
   if cap['decision']['state'] not in {'required','supported'}: continue
   checks=cap.get('verification',{}).get('checks',[])
   for check in checks:
    for mode in check['modes']: contracts[(cap['id'],mode)].append({'kind':check['kind'],'ref':check['ref']})
   for mode,state in cap['modes'].items():
    if state=='not-applicable': continue
    key=(cap['id'],mode); found=candidates[key]
    explicit=[{**check,'recordedOutcomes':sorted(outcome_methods[method(check['ref'])])} for check in contracts[key]]
    contract_status='not-contracted' if not explicit else 'passing' if all(check['recordedOutcomes']==['Passed'] for check in explicit) else 'not-run' if all(not check['recordedOutcomes'] for check in explicit) else 'partial-or-failing'
    rows.append({'capability':cap['id'],'section':section['id'],'mode':mode,'currentState':state,'testCandidates':found[:160],'explicitContracts':explicit,'explicitContractStatus':contract_status,'benchmarkScenarios':[b['id'] for b in benchmarks if cap['id'] in b['capabilities']]})
 summary={'targetModes':len(rows),'modesWithTestCandidates':sum(bool(r['testCandidates']) for r in rows),'modesWithExplicitContracts':sum(bool(r['explicitContracts']) for r in rows),'modesWithPassingExplicitContracts':sum(r['explicitContractStatus']=='passing' for r in rows),'modesWithRecordedPassingCandidate':sum(any(x['recordedOutcomes']==['Passed'] for x in r['testCandidates']) for r in rows),'testMethods':len(all_tests),'testMethodsWithCapabilityCandidate':sum(bool(r['parentCapabilityModes']) for r in all_tests.values()),'benchmarkScenarios':len(benchmarks),'benchmarkScenariosWithHarness':sum(b['status']=='existing-harness' for b in benchmarks)}
 OUT.write_text(json.dumps({'schemaVersion':1,'generatedBy':'scripts/build-pdf-evidence-attribution.py','policy':'Candidate attribution measures discoverable test/benchmark relevance. Only explicit reviewed contracts affect strict support; test outcomes are recorded-run evidence only.','modes':rows,'summary':summary},indent=2)+'\n'); print(f'wrote {OUT} with {summary["modesWithTestCandidates"]}/{summary["targetModes"]} modes having test candidates')
if __name__=='__main__': main()
