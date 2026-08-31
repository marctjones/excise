#!/usr/bin/env python3
"""Compare pinned machine-readable PDF sources with their upstream default branches."""
from __future__ import annotations
import json, subprocess
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def main():
 reg=json.loads((ROOT/'test-pdfs/manifests/pdf-spec-registry/registry.json').read_text())
 tracked={
  'arlington-pdf-model': ('pdf-association/arlington-pdf-model', 'master'),
  'pdf-issues': ('pdf-association/pdf-issues', 'main'),
 }
 results=[]
 for source_id,(repository,branch) in tracked.items():
  source=next(x for x in reg['sources'] if x['id']==source_id)
  upstream=subprocess.run(['gh','api',f'repos/{repository}/commits/{branch}','--jq','.sha'],text=True,capture_output=True,check=True).stdout.strip()
  results.append({'source':source_id,'pinned':source['revision'],'upstream':upstream,'changed':source['revision']!=upstream})
 print(json.dumps({'sources':results,'changed':any(item['changed'] for item in results)},indent=2))
 return 1 if any(item['changed'] for item in results) else 0
if __name__=='__main__': raise SystemExit(main())
