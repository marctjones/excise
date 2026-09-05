#!/usr/bin/env python3
"""Import TRX test outcomes with host/revision provenance for capability review."""
from __future__ import annotations
import argparse, hashlib, json, platform, subprocess
from datetime import datetime, timezone
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT=Path(__file__).resolve().parents[1]
REG=ROOT/'test-pdfs/manifests/pdf-spec-registry'
OUT=REG/'generated/test-outcomes.json'
def rel(path):
 try: return str(path.resolve().relative_to(ROOT))
 except ValueError: return str(path)
def command(cmd):
 return subprocess.run(cmd,shell=True,cwd=ROOT,text=True,capture_output=True).stdout.strip() or 'unavailable'
def main():
 parser=argparse.ArgumentParser(description=__doc__)
 parser.add_argument('--trx',action='append',type=Path,help='TRX file; repeatable')
 parser.add_argument('--output',type=Path,default=OUT)
 args=parser.parse_args()
 policy=json.loads((REG/'test-outcomes.json').read_text())
 files=sorted(set(args.trx or [path for pattern in policy['resultDiscovery'] for path in ROOT.glob(pattern)]), key=lambda path:(path.stat().st_mtime_ns, str(path)))
 outcomes=[]
 result_files=[]
 for file in files:
  root=ET.parse(file).getroot()
  result_files.append({'path':rel(file),'modifiedAt':datetime.fromtimestamp(file.stat().st_mtime, timezone.utc).isoformat(),'sha256':hashlib.sha256(file.read_bytes()).hexdigest()})
  for item in root.findall('.//{*}UnitTestResult'):
   outcomes.append({'testName':item.get('testName'),'outcome':item.get('outcome'),'duration':item.get('duration'),'trx':rel(file),'resultModifiedAt':result_files[-1]['modifiedAt'],'trxSha256':result_files[-1]['sha256']})
 # Results directories accumulate runs.  Keep the newest result for each test
 # deterministically instead of counting stale passes alongside a newer failure.
 latest={}
 for outcome in outcomes:
  latest[outcome['testName']]=outcome
 outcomes=sorted(latest.values(), key=lambda item:item['testName'])
 result={'schemaVersion':1,'generatedBy':'scripts/collect-pdf-test-outcomes.py','policy':policy['policy'],'recordedAt':datetime.now(timezone.utc).isoformat(),'gitRevision':command('git rev-parse HEAD'),'dotnetVersion':command('dotnet --version'),'host':platform.platform(),'resultFiles':result_files,'outcomes':outcomes,'summary':{'trxFiles':len(files),'tests':len(outcomes),'outcomes':{key:sum(item['outcome']==key for item in outcomes) for key in sorted({item['outcome'] for item in outcomes})},'status':'recorded' if outcomes else 'no-trx-results-imported'}}
 args.output.write_text(json.dumps(result,indent=2)+'\n')
 print(f"wrote {args.output} with {len(outcomes)} test outcomes")
if __name__=='__main__': main()
