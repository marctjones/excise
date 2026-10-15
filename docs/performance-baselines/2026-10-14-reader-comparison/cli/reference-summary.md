# excise-cli vs reference renderers, one page, fresh process each, median of 3 (CLEAN rerun 2026-10-14, load ~3.7)

Release `dotnet build` output (JIT), not the shipping R2R/AOT shape, so excise's ~55-70 ms startup is pessimistic.

| fixture | wall ms excise / mutool / cairo / gs / pdfbox | excise render ms | CPU ms excise / mutool | RSS MiB excise / mutool / cairo / gs / pdfbox | wall vs mutool | CPU vs mutool | RSS vs mutool | excise wall, loaded run |
|---|---|---:|---|---|---:|---:|---:|---:|
| plans-soft-masks-36dpi | 184 / 92 / 777 / 248 / 1803 | 117 | 157 / 71 | 90 / 15 / 46 / 28 / 483 | 2.0x | 2.2x | 6.0x | 174 |
| plans-soft-masks-150dpi | 327 / 251 / 1289 / 652 / 2310 | 255 | 307 / 223 | 145 / 79 / 63 / 47 / 574 | 1.3x | 1.4x | 1.8x | 287 |
| acc-compensation-cover | 900 / 258 / 1179 / 412 / 1686 | 802 | 3065 / 224 | 196 / 70 / 74 / 34 / 363 | 3.5x | 13.7x | 2.8x | 831 |
| multilingual-cjk | 142 / 44 / 157 / 93 / 756 | 70 | 115 / 25 | 83 / 19 / 25 / 42 / 109 | 3.2x | 4.6x | 4.4x | 126 |
| irs-w9-form | 181 / 65 / 382 / 186 / 1011 | 108 | 155 / 37 | 86 / 20 / 25 / 36 / 121 | 2.8x | 4.2x | 4.3x | 163 |
| altona-technical2-p1 | 2585 / 2289 / 4731 / 3681 / 11000 | 2487 | 2884 / 2246 | 188 / 450 / 327 / 88 / 1159 | 1.1x | 1.3x | 0.4x | 2148 |
| altona-technical2-p5 | 524 / 429 / 884 / 756 / 2755 | 441 | 486 / 412 | 119 / 54 / 107 / 27 / 377 | 1.2x | 1.2x | 2.2x | 435 |
| altona-technical2-p7 | 303 / 224 / 563 / 396 / 1541 | 234 | 277 / 207 | 92 / 24 / 109 / 33 / 446 | 1.4x | 1.3x | 3.9x | 287 |
| scotus-opinion-p1 | 152 / 48 / 241 / 198 / 1106 | 77 | 124 / 24 | 82 / 19 / 31 / 37 / 124 | 3.2x | 5.2x | 4.4x | 150 |
| irs-1040-p1 | 196 / 73 / 350 / 195 / 1034 | 110 | 156 / 50 | 87 / 21 / 26 / 37 / 139 | 2.7x | 3.1x | 4.1x | 166 |
| indexed-4bit-33mp-image | 196 / 232 / 258 / 98 / 1634 | 115 | 224 / 207 | 85 / 141 / 148 / 24 / 308 | 0.8x | 1.1x | 0.6x | 168 |
| fifty-transparency-groups | 403 / 162 / 841 / 1383 / 2726 | 316 | 376 / 138 | 193 / 60 / 45 / 42 / 312 | 2.5x | 2.7x | 3.2x | 357 |
| mesh-shading-type7 | 519 / 125 / 142 / 239 / 1203 | 441 | 489 / 100 | 132 / 35 / 40 / 30 / 237 | 4.2x | 4.9x | 3.7x | 468 |
| nested-forms-dct | 217 / 123 / 130 / 310 / 1508 | 136 | 182 / 99 | 102 / 54 / 47 / 30 / 259 | 1.8x | 1.8x | 1.9x | 199 |
