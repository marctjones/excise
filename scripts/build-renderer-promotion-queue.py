#!/usr/bin/env python3
"""Turn renderer evidence inventories into reviewable mode-promotion contracts."""
from __future__ import annotations
import json
from collections import Counter
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
REG=ROOT/'test-pdfs/manifests/pdf-spec-registry'
OUT=REG/'generated/renderer-promotion-queue.json'
PARENTS={'pdf.17.content.streams','pdf.17.graphics.images','pdf.17.graphics.fonts','pdf.17.transparency.model'}

def load(path): return json.loads(path.read_text(encoding='utf-8'))
def links(record, capability, mode):
 return any(link['capability']==capability and mode in link['modes'] for link in record['parentCapabilityModes'])
def main():
 registry=load(REG/'registry.json')
 rows=[cap for section in registry['sections'] for cap in load(REG/section['path'])['capabilities'] if cap['id'] in PARENTS]
 sources=load(REG/'generated/implementation-evidence-map.json')['sources']
 tests=load(REG/'generated/renderer-test-evidence-map.json')['tests']
 contracts=[]
 for cap in rows:
  for mode,state in cap['modes'].items():
   if state=='not-applicable': continue
   source_candidates=[item['id'] for item in sources if links(item,cap['id'],mode)]
   test_candidates=[item['id'] for item in tests if links(item,cap['id'],mode)]
   independent=[item['id'] for item in tests if links(item,cap['id'],mode) and item['evidenceKind'] in {'differential-candidate','corpus-candidate'}]
   contracts.append({'capability':cap['id'],'mode':mode,'currentState':state,'promotion':'review-required','implementationCandidates':source_candidates,'directTestCandidates':test_candidates,'independentEvidenceCandidates':independent,'requiredBeforePartial':['review one or more implementation candidates and confirm responsibility','review one or more direct test candidates and record the exact asserted behavior','record a known limitation or an explicit out-of-scope boundary'],'requiredBeforeStrict':['add or identify an atomic fixture that isolates the feature','accept a differential or corpus oracle for the intended behavior','set implemented only after all required mode evidence is reviewed and the verification contract is executable']})
 summary={'contracts':len(contracts),'withImplementationCandidates':sum(bool(x['implementationCandidates']) for x in contracts),'withDirectTestCandidates':sum(bool(x['directTestCandidates']) for x in contracts),'withIndependentCandidates':sum(bool(x['independentEvidenceCandidates']) for x in contracts),'byCapability':dict(Counter(x['capability'] for x in contracts))}
 OUT.write_text(json.dumps({'schemaVersion':1,'generatedBy':'scripts/build-renderer-promotion-queue.py','policy':'Candidate lists are review inputs. They must be promoted into a capability leaf explicitly; source/test name association alone is not evidence of conformance.','contracts':contracts,'summary':summary},indent=2)+'\n')
 print(f"wrote {OUT} with {len(contracts)} renderer mode-promotion contracts")
if __name__=='__main__': main()
