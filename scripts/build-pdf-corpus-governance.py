#!/usr/bin/env python3
"""Derive a provenance inventory from the existing corpus registry."""
from __future__ import annotations
import argparse, csv, hashlib, json, subprocess
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'test-pdfs/manifests/pdf-spec-registry/generated/corpus-governance.json'
def digest(path):
 h=hashlib.sha256()
 with path.open('rb') as stream:
  for chunk in iter(lambda:stream.read(1024*1024),b''): h.update(chunk)
 return h.hexdigest()
def consumers(path):
 result=subprocess.run(['git','grep','-l','--',path],cwd=ROOT,text=True,capture_output=True)
 if result.returncode not in {0,1}: raise RuntimeError(result.stderr.strip())
 return [item for item in result.stdout.splitlines() if item.endswith(('.cs','.py','.sh','.json','.tsv')) and not item.startswith('test-pdfs/')]
def main():
 parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('--hash-files',action='store_true'); parser.add_argument('--output',type=Path,default=OUT); args=parser.parse_args()
 rows=[]
 with (ROOT/'tests/corpora.tsv').open() as f:
  for row in csv.reader((line for line in f if not line.startswith('#')),delimiter='\t'):
   if len(row)<7: continue
   name,tier,path,script,size,terms,purpose=row[:7]
   location=ROOT/path
   pdfs=list(location.rglob('*.pdf')) if location.is_dir() else []
   asset={'id':name,'tier':tier,'path':path,'acquisition':script,'acquisitionVerification':'scripts/corpus.sh verify and a release-time SHA-256 inventory','estimatedSize':size,'terms':terms,'purpose':purpose,'featureBugSpecCoverage':purpose,'expectedOutcomePolicy':'No global pass/fail is inferred from corpus membership; each named test consumer owns its expected result and oracle contract.','present':bool(pdfs),'pdfCount':len(pdfs),'safetyTier':'untrusted-input','owner':'PDF capability registry','consumerReferences':consumers(path)}
   if args.hash_files:
    asset['files']=[{'path':str(p.relative_to(ROOT)),'bytes':p.stat().st_size,'sha256':digest(p)} for p in sorted(pdfs)]
    asset['identity']='per-file SHA-256'
   else: asset['identity']='acquisition identity; use --hash-files before release baselines'
   rows.append(asset)
 policy=json.loads((ROOT/'test-pdfs/manifests/pdf-spec-registry/corpus-policy.json').read_text())
 args.output.write_text(json.dumps({'schemaVersion':1,'generatedBy':'scripts/build-pdf-corpus-governance.py','sourceOfTruth':'tests/corpora.tsv','policy':'corpus-policy.json','hashingEnabled':args.hash_files,'assets':rows,'stabilityContracts':policy['stabilityContracts']},indent=2)+'\n')
 print(f'wrote {args.output} with {len(rows)} corpus sources')
if __name__=='__main__': main()
