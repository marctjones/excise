import zlib, sys
# One Text annotation per /Name value, plus opacity and border-style probes.
NAMES = ["Comment","Key","Note","Help","NewParagraph","Paragraph","Insert"]
content=[]
def line(x,y,s,size=9): content.append(f"BT /F1 {size} Tf {x} {y} Td ({s}) Tj ET")
line(50,752,"Text annotation /Name variants",14)
for i,n in enumerate(NAMES):
    line(50+i*75, 700, n, 8)
line(50,600,"Opacity /CA 1.0 / 0.5 / 0.2 on Square",11)
line(50,480,"Border style: solid vs dashed (/BS /D)",11)
line(50,360,"Highlight with NO /C (viewer default colour)",11)
stream="\n".join(content).encode("latin-1"); comp=zlib.compress(stream)
objs={1:b"<< /Type /Catalog /Pages 2 0 R >>",2:b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
      5:b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
      4:b"<< /Length %d /Filter /FlateDecode >>\nstream\n"%len(comp)+comp+b"\nendstream"}
ann=[]
for i,n in enumerate(NAMES):
    x=50+i*75
    ann.append(f"/Type /Annot /Subtype /Text /Rect [{x} 715 {x+24} 739] /Name /{n} "
               f"/C [1 0.85 0.2] /F 4 /T (d) /Contents ({n})")
for i,ca in enumerate(["1.0","0.5","0.2"]):
    x=50+i*110
    ann.append(f"/Type /Annot /Subtype /Square /Rect [{x} 530 {x+90} 590] "
               f"/C [0.8 0.1 0.1] /IC [0.2 0.4 0.9] /CA {ca} /F 4 /T (d) /Contents (CA {ca})")
ann.append("/Type /Annot /Subtype /Square /Rect [50 400 200 460] /C [0 0 0] "
           "/BS << /W 3 /S /S >> /F 4 /T (d) /Contents (solid 3)")
ann.append("/Type /Annot /Subtype /Square /Rect [230 400 380 460] /C [0 0 0] "
           "/BS << /W 3 /S /D /D [6 3] >> /F 4 /T (d) /Contents (dashed 3)")
ann.append("/Type /Annot /Subtype /Highlight /Rect [48 352 340 372] "
           "/QuadPoints [48 372 340 372 48 352 340 352] /F 4 /T (d) /Contents (no colour)")
first=6
refs=" ".join(f"{first+i} 0 R" for i in range(len(ann)))
for i,a in enumerate(ann): objs[first+i]=("<< "+a+" >>").encode("latin-1")
objs[3]=("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> "
         f"/Contents 4 0 R /Annots [{refs}] >>").encode("latin-1")
out=bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n"); off={}
for n in sorted(objs):
    off[n]=len(out); out+=b"%d 0 obj\n"%n+objs[n]+b"\nendobj\n"
x=len(out); N=max(objs)+1
out+=b"xref\n0 %d\n0000000000 65535 f \n"%N
for n in range(1,N): out+=b"%010d 00000 n \n"%off.get(n,0)
out+=b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n"%(N,x)
open(sys.argv[1],"wb").write(bytes(out)); print("wrote",sys.argv[1],len(ann),"annots")
