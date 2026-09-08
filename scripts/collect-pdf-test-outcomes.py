#!/usr/bin/env python3
"""Import TRX test outcomes with host/revision provenance for capability review.

Default behavior REPLACES the outcome set with exactly what the given/
discovered TRX files contain -- correct for a full-tier run, where every
project ran and a removed test's stale outcome should disappear too.

--merge instead layers those outcomes onto the file --output already
holds (or the committed snapshot, if any), keeping every existing entry
whose test name isn't in the new TRX. Use this for a dev-loop refresh
that only re-ran SOME projects (e.g. Core.Tests + Rendering.Tests after
wiring registry checks that cite only those two) -- it moves the modes
you actually touched without needing a full run (Excise.App.Tests alone
is 8-17 minutes, serial by design, #363) just to avoid regressing the
untouched projects' recorded evidence to nothing.
"""
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
 parser=argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
 parser.add_argument('--trx',action='append',type=Path,help='TRX file; repeatable')
 parser.add_argument('--output',type=Path,default=OUT)
 parser.add_argument('--merge',action='store_true',help='layer new outcomes onto the existing snapshot instead of replacing it wholesale (dev-loop / partial refresh)')
 args=parser.parse_args()
 policy=json.loads((REG/'test-outcomes.json').read_text())
 files=sorted(set(args.trx or [path for pattern in policy['resultDiscovery'] for path in ROOT.glob(pattern)]), key=lambda path:(path.stat().st_mtime_ns, str(path)))
 if not files:
  raise SystemExit('no TRX files found (pass --trx, or check resultDiscovery globs match something)')
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
 if args.merge and args.output.is_file():
  existing=json.loads(args.output.read_text())
  new_paths={rf['path'] for rf in result_files}
  # A result-file row is superseded only if THIS run re-imported that exact
  # trx path; every other prior project's row (e.g. App.Tests, untouched
  # tonight) is kept as-is, same as its outcomes below.
  result_files=[rf for rf in existing.get('resultFiles',[]) if rf['path'] not in new_paths]+result_files
  merged={o['testName']:o for o in existing.get('outcomes',[])}
  merged.update(latest)
  latest=merged
 outcomes=sorted(latest.values(), key=lambda item:item['testName'])
 generated_by='scripts/collect-pdf-test-outcomes.py --merge' if args.merge else 'scripts/collect-pdf-test-outcomes.py'
 result={'schemaVersion':1,'generatedBy':generated_by,'policy':policy['policy'],'recordedAt':datetime.now(timezone.utc).isoformat(),'gitRevision':command('git rev-parse HEAD'),'dotnetVersion':command('dotnet --version'),'host':platform.platform(),'resultFiles':result_files,'outcomes':outcomes,'summary':{'trxFiles':len(result_files),'tests':len(outcomes),'outcomes':{key:sum(item['outcome']==key for item in outcomes) for key in sorted({item['outcome'] for item in outcomes})},'status':'recorded' if outcomes else 'no-trx-results-imported'}}
 args.output.write_text(json.dumps(result,indent=2)+'\n')
 print(f"wrote {args.output} with {len(outcomes)} test outcomes"+(' (merged)' if args.merge else ''))
if __name__=='__main__': main()
