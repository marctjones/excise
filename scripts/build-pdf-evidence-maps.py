#!/usr/bin/env python3
"""Build complete source-ownership and xUnit test evidence inventories for PDFE."""
from __future__ import annotations
import hashlib, json, re
from collections import Counter, defaultdict
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
REG=ROOT/'test-pdfs/manifests/pdf-spec-registry'
CONFIG=REG/'evidence-maps.json'
OUT=REG/'generated'
CLASS=re.compile(r'\bclass\s+(\w+)')
METHOD=re.compile(r'^\s*public\s+(?:async\s+)?(?:void|Task(?:<[^>]+>)?)\s+(\w+)\s*\(')

def load(path): return json.loads(path.read_text(encoding='utf-8'))
def registry_links():
 """Return only mode-specific links explicitly declared by the registry.

 Filename heuristics stay discovery-only.  A link here exists because the
 registry itself names a source owner or a verification check and is still
 marked review-required in generated output.
 """
 source_links=defaultdict(list); test_links=defaultdict(list)
 registry=load(REG/'registry.json')
 for section in registry['sections']:
  for cap in load(REG/section['path'])['capabilities']:
   tracking=cap.get('tracking', {})
   roles=[mode for mode in tracking.get('processorRoles', []) if cap.get('modes', {}).get(mode)!='not-applicable']
   for ref in tracking.get('implementationRefs', []):
    source_links[ref.split('::',1)[0]].append({'capability':cap['id'],'modes':roles,'mappingSource':'registry-implementation-owner'})
   for check in cap.get('verification',{}).get('checks',[]):
    test_links[check['ref'].split('::',1)[0], check['ref'].rsplit('::',1)[-1]].append({'capability':cap['id'],'modes':check['modes'],'mappingSource':'registry-verification-contract'})
 return source_links,test_links
def test_methods(path):
 current=None; attrs=[]; out=[]
 for number,line in enumerate(path.read_text(encoding='utf-8',errors='ignore').splitlines(),1):
  if match:=CLASS.search(line): current=match.group(1)
  stripped=line.strip()
  if stripped.startswith('[') or (attrs and not stripped.startswith(('public ','private ','protected ','internal ')) and (stripped.endswith(')') or stripped.endswith(']'))): attrs.append(stripped); continue
  if match:=METHOD.match(line):
   text='\n'.join(attrs)
   if 'Fact' in text or 'Theory' in text: out.append({'path':str(path.relative_to(ROOT)),'class':current or '<unknown>','method':match.group(1),'line':number,'xunitKind':'theory' if 'Theory' in text else 'fact'})
   attrs=[]; continue
  if stripped and not stripped.startswith('//'): attrs=[]
 return out
def classify(subject, facets):
 subject=subject.lower().replace('_','').replace('-','')
 assigned=[facet for facet in facets if facet['patterns'] and any(pattern in subject for pattern in facet['patterns'])]
 return assigned or [next(facet for facet in facets if facet['id']=='product-integration-general')]
def mapped_record(base, facets, declared_links=()):
 parents=defaultdict(set)
 sources=defaultdict(set)
 for facet in facets:
  for parent in facet['parents']:
   parents[parent].update(facet['modes']); sources[parent].add('keyword-facet-candidate')
 for link in declared_links:
  parents[link['capability']].update(link['modes']); sources[link['capability']].add(link['mappingSource'])
 return {**base,'facets':[facet['id'] for facet in facets],'parentCapabilityModes':[{'capability':cap,'modes':sorted(modes),'mappingSources':sorted(sources[cap])} for cap,modes in sorted(parents.items())],'promotion':'review-required'}
def summary(rows):
 facets=Counter(f for row in rows for f in row['facets']); parents=Counter()
 for row in rows:
  for link in row['parentCapabilityModes']:
   for mode in link['modes']: parents[f"{link['capability']}:{mode}"]+=1
 return {'records':len(rows),'facets':dict(sorted(facets.items())),'parentCapabilityModes':dict(sorted(parents.items())),'fallbackRecords':facets['product-integration-general'],'allRecordsMapped':len(rows)==sum(bool(row['facets']) for row in rows)}
def main():
 config=load(CONFIG); facets=config['facets']; source_declared,test_declared=registry_links(); source_rows=[]; test_rows=[]; expected_sources=0; expected_tests=0; digest=hashlib.sha256()
 for root_name in config['sourceRoots']:
  for path in sorted((ROOT/root_name).rglob('*.cs')):
   if '/bin/' in str(path) or '/obj/' in str(path): continue
   relative=str(path.relative_to(ROOT)); digest.update(relative.encode())
   expected_sources+=1
   source_rows.append(mapped_record({'id':relative,'path':relative,'kind':'source-file'},classify(relative,facets),source_declared[relative]))
 for root_name in config['testRoots']:
  for path in sorted((ROOT/root_name).rglob('*.cs')):
   if '/bin/' in str(path) or '/obj/' in str(path): continue
   for test in test_methods(path):
    test_id=f"{test['path']}::{test['class']}.{test['method']}"; digest.update(test_id.encode())
    expected_tests+=1
    declared=test_declared[test['path'],test['method']]
    test_rows.append(mapped_record({'id':test_id,**test,'kind':'test-method'},classify(test_id,facets),declared))
 if len(source_rows)!=expected_sources or len(test_rows)!=expected_tests:
  raise RuntimeError(f"collector completeness failure: sources {len(source_rows)}/{expected_sources}, tests {len(test_rows)}/{expected_tests}")
 source={'schemaVersion':1,'generatedBy':'scripts/build-pdf-evidence-maps.py','policy':config['policy'],'sourceFingerprint':digest.hexdigest(),'sources':source_rows,'summary':summary(source_rows)}
 tests={'schemaVersion':1,'generatedBy':'scripts/build-pdf-evidence-maps.py','policy':config['policy'],'sourceFingerprint':digest.hexdigest(),'tests':test_rows,'summary':summary(test_rows)}
 OUT.mkdir(parents=True,exist_ok=True)
 (OUT/'implementation-evidence-map.json').write_text(json.dumps(source,indent=2)+'\n')
 (OUT/'test-suite-evidence-map.json').write_text(json.dumps(tests,indent=2)+'\n')
 print(f"wrote source map ({len(source_rows)} files) and test map ({len(test_rows)} xUnit methods)")
if __name__=='__main__': main()
