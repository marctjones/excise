#!/usr/bin/env python3
"""Generate leaf inventories from the remaining pre-registry matrices."""
from __future__ import annotations
import json
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
REG=ROOT/'test-pdfs/manifests/pdf-spec-registry'

def write(name, caps):
 (REG/'sections'/f'{name}.json').write_text(json.dumps({'$schema':'../schemas/section.schema.json','schemaVersion':1,'section':name,'capabilities':caps},indent=2)+'\n')

def cap(i,name,decision,modes,evidence,notes):
 return {'id':i,'name':name,'spec':[{'source':'iso-32000-2','clauses':['Annex A']}],'classification':'core','decision':{'state':decision,'rationale':'Generated from the active legacy coverage matrix; refine product intent at the leaf.'},'modes':modes,'evidence':evidence,'verification':{'status':'planned','requiredModes':list(modes),'checks':[{'kind':'unit','ref':f'legacy matrix evidence for {name}','modes':list(modes)}]},'notes':notes,'documentation':['test-pdfs/manifests/pdf20-renderer-requirements.json']}

def main():
 renderer=json.load(open(ROOT/'test-pdfs/manifests/pdf20-renderer-requirements.json'))['requirements']
 hard={'RendererCore','ParserSupport','SecurityPolicy'}
 write('renderer-requirements',[cap(f'pdf.20.renderer.requirement-{n:03d}',r['id'],'required' if r['profile'] in hard else 'supported',{'parse':'unknown','render':'unknown','preserve':'unknown'},[{'kind':'legacy','ref':r['id']}],r['obligation']) for n,r in enumerate(renderer,1)])
 image=json.load(open(ROOT/'test-pdfs/manifests/pdf-image-feature-matrix.json'))['requirements']
 write('image-requirements',[cap(f'pdf.20.image.requirement-{n:03d}',r['id'],'required',{'parse':'unknown','render':'unknown','preserve':'unknown'},[{'kind':'legacy','ref':r['id']}],r['requiredBy']) for n,r in enumerate(image,1)])
 annotations=json.load(open(ROOT/'tests/annotation-support-matrix.json'))['subtypes']
 caps=[]
 for n,r in enumerate(annotations,1):
  evidence=[] if not r.get('verifiedBy') else [{'kind':'unit','ref':r['verifiedBy']}]
  caps.append(cap(f'pdf.20.annotation.subtype-{n:03d}',r['subtype'],'required',{'parse':'unknown','preserve':'unknown','render':'unknown','extract':'unknown'},evidence,r['note']))
 write('annotation-subtypes',caps)
 print(f'wrote {len(renderer)} renderer, {len(image)} image, and {len(annotations)} annotation leaves')
if __name__=='__main__': main()
