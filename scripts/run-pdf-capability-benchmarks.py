#!/usr/bin/env python3
"""Run registry-declared benchmark commands and write machine-readable timing evidence."""
from __future__ import annotations
import argparse, json, math, platform, re, subprocess, time
from datetime import datetime, timezone
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
MANIFEST=ROOT/'test-pdfs/manifests/pdf-spec-registry/benchmarks.json'
def main():
 p=argparse.ArgumentParser(description=__doc__); p.add_argument('--scenario',action='append'); p.add_argument('--output',type=Path,default=ROOT/'test-pdfs/manifests/pdf-spec-registry/generated/benchmark-results.json'); p.add_argument('--dry-run',action='store_true'); p.add_argument('--check-baseline',action='store_true'); p.add_argument('--runs',type=int,default=3); a=p.parse_args()
 if a.runs < 1: parser.error('--runs must be positive')
 data=json.loads(MANIFEST.read_text()); baselines=json.loads((MANIFEST.parent/data['baselineManifest']).read_text())['baselines']; chosen=[x for x in data['scenarios'] if not a.scenario or x['id'] in a.scenario]
 if a.scenario and len(chosen)!=len(set(a.scenario)): raise SystemExit('unknown benchmark scenario')
 results=[]
 for x in chosen:
  if x['status'] != 'existing-harness':
   results.append({'id':x['id'],'status':'unmeasured','reason':'no dedicated benchmark harness registered','metricsRequested':x['metrics']})
   continue
  samples=[]; output=[]; rc=0
  for _ in range(a.runs):
   started=time.monotonic()
   run=subprocess.run(x['command'],shell=True,cwd=ROOT,text=True,capture_output=True) if not a.dry_run else None
   elapsed=round(time.monotonic()-started,3)
   samples.append(elapsed); output.append('' if run is None else run.stdout + run.stderr)
   rc=0 if run is None else run.returncode
   if rc: break
  ordered=sorted(samples); percentile=lambda quantile: ordered[min(len(ordered)-1, max(0, math.ceil(quantile*len(ordered))-1))]
  budget=baselines.get(x['id'],{}).get('maxWallSeconds'); baseline_ok=budget is None or max(samples,default=0)<=budget
  observed={'wall_p50_seconds':percentile(.5),'wall_p95_seconds':percentile(.95),'wall_samples_seconds':samples}
  latency=[int(value) for text in output for value in re.findall(r'(?:median=| in )(\\d+)ms', text)]
  if latency: observed['reported_latency_samples_ms']=latency
  unmeasured=[metric for metric in x['metrics'] if metric not in {'latency_ms','median_latency_ms_per_page'}]
  results.append({'id':x['id'],'status':x['status'],'command':x['command'],'exitCode':rc if baseline_ok or not a.check_baseline else 1,'runsRequested':a.runs,'maxWallSeconds':budget,'baselinePass':baseline_ok,'metricsRequested':x['metrics'],'metricsObserved':observed,'unmeasuredMetrics':unmeasured})
  if rc: break
 def capture(command):
  return subprocess.run(command, shell=True, cwd=ROOT, text=True, capture_output=True).stdout.strip() or 'unavailable'
 a.output.write_text(json.dumps({'schemaVersion':1,'generatedBy':'scripts/run-pdf-capability-benchmarks.py','recordedAt':datetime.now(timezone.utc).isoformat(),'host':platform.platform(),'gitRevision':capture('git rev-parse HEAD'),'dotnetVersion':capture('dotnet --version'),'results':results},indent=2)+'\n')
 try: display=a.output.relative_to(ROOT)
 except ValueError: display=a.output
 print(f'wrote {display}')
 raise SystemExit(next((x.get('exitCode', 0) for x in results if x.get('exitCode', 0)),0))
if __name__=='__main__': main()
