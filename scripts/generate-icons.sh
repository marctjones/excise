#!/usr/bin/env bash
#
# Regenerate the app icon raster set from the vector master (#679).
#
# The user-facing icon lives as one SVG (Excise.App/Assets/excise_logo.svg);
# the shipped app needs a PNG set + a multi-size .ico. This script rebuilds
# them reproducibly so an icon change is a one-command regeneration instead of
# an ad-hoc manual export.
#
# Rasterizer: prefers a real SVG rasterizer (rsvg-convert / resvg / inkscape /
# ImageMagick) and falls back to headless Chrome, which every CI runner has.
# The .ico is packed from the PNGs with Python (stdlib only) or ImageMagick.
#
# Usage:
#   scripts/generate-icons.sh            # regenerate the committed assets
#   scripts/generate-icons.sh --check    # regenerate to a temp dir; fail if a
#                                         # size is missing/invalid (dimensions),
#                                         # NOT a byte-diff (encoders vary)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SVG="$ROOT/Excise.App/Assets/excise_logo.svg"
SIZES=(16 32 64 128 256)
CHECK=0
[[ "${1:-}" == "--check" ]] && CHECK=1

[[ -f "$SVG" ]] || { echo "error: SVG master not found: $SVG" >&2; exit 1; }

if [[ "$CHECK" == 1 ]]; then
  OUT="$(mktemp -d)"; trap 'rm -rf "$OUT"' EXIT
else
  OUT="$ROOT/Excise.App/Assets"
fi

# ── pick a rasterizer ────────────────────────────────────────────────────────
render_png() {  # render_png <size> <out.png>
  local size="$1" out="$2"
  if command -v rsvg-convert >/dev/null 2>&1; then
    rsvg-convert -w "$size" -h "$size" "$SVG" -o "$out"
  elif command -v resvg >/dev/null 2>&1; then
    resvg -w "$size" -h "$size" "$SVG" "$out"
  elif command -v inkscape >/dev/null 2>&1; then
    inkscape "$SVG" -w "$size" -h "$size" -o "$out" >/dev/null 2>&1
  elif command -v magick >/dev/null 2>&1; then
    magick -background none -density 384 "$SVG" -resize "${size}x${size}" "$out"
  elif command -v convert >/dev/null 2>&1; then
    convert -background none -density 384 "$SVG" -resize "${size}x${size}" "$out"
  else
    render_png_chrome "$size" "$out"
  fi
}

CHROME_BIN=""
find_chrome() {
  [[ -n "$CHROME_BIN" ]] && return 0
  for c in google-chrome google-chrome-stable chromium chromium-browser \
           "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" \
           "/Applications/Chromium.app/Contents/MacOS/Chromium"; do
    if command -v "$c" >/dev/null 2>&1 || [[ -x "$c" ]]; then CHROME_BIN="$c"; return 0; fi
  done
  echo "error: no SVG rasterizer found (install rsvg-convert/resvg/inkscape/ImageMagick, or Chrome)" >&2
  exit 1
}

render_png_chrome() {  # headless Chrome fallback (transparent, exact size)
  local size="$1" out="$2"; find_chrome
  local html; html="$(mktemp).html"
  printf '<!doctype html><meta charset=utf-8><style>html,body{margin:0;background:transparent}svg{display:block;width:%spx;height:%spx}</style>%s\n' \
    "$size" "$size" "$(cat "$SVG")" > "$html"
  "$CHROME_BIN" --headless=new --disable-gpu --hide-scrollbars \
    --force-device-scale-factor=1 --default-background-color=00000000 \
    --window-size="$size,$size" --screenshot="$out" "file://$html" >/dev/null 2>&1
  rm -f "$html"
}

echo "==> rendering PNGs from $(basename "$SVG")"
for s in "${SIZES[@]}"; do
  render_png "$s" "$OUT/excise_logo_${s}.png"
  echo "    ${s}x${s} -> excise_logo_${s}.png"
done

# ── pack the .ico from the PNGs ──────────────────────────────────────────────
echo "==> packing excise_logo.ico"
if command -v python3 >/dev/null 2>&1; then
  python3 - "$OUT" "${SIZES[@]}" <<'PY'
import struct, sys, os
out = sys.argv[1]; sizes = [int(s) for s in sys.argv[2:]]
imgs = [(s, open(os.path.join(out, f"excise_logo_{s}.png"), "rb").read()) for s in sizes]
hdr = struct.pack("<HHH", 0, 1, len(imgs))
off = 6 + 16 * len(imgs)
entries = b""; blobs = b""
for s, data in imgs:
    w = h = (0 if s == 256 else s)          # 0 == 256 in the ICO dir
    entries += struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(data), off)
    off += len(data); blobs += data
open(os.path.join(out, "excise_logo.ico"), "wb").write(hdr + entries + blobs)
PY
elif command -v magick >/dev/null 2>&1; then
  magick "$OUT/excise_logo_16.png" "$OUT/excise_logo_32.png" "$OUT/excise_logo_64.png" \
         "$OUT/excise_logo_128.png" "$OUT/excise_logo_256.png" "$OUT/excise_logo.ico"
else
  echo "error: need python3 (stdlib) or ImageMagick to pack the .ico" >&2
  exit 1
fi

# ── check mode: validate dimensions, not bytes (encoders differ) ─────────────
if [[ "$CHECK" == 1 ]]; then
  fail=0
  for s in "${SIZES[@]}"; do
    f="$OUT/excise_logo_${s}.png"
    [[ -s "$f" ]] || { echo "MISSING: excise_logo_${s}.png"; fail=1; continue; }
    if command -v sips >/dev/null 2>&1; then
      w=$(sips -g pixelWidth "$f" 2>/dev/null | awk '/pixelWidth/{print $2}')
      [[ "$w" == "$s" ]] || { echo "WRONG SIZE: excise_logo_${s}.png is ${w}px"; fail=1; }
    fi
  done
  [[ -s "$OUT/excise_logo.ico" ]] || { echo "MISSING: excise_logo.ico"; fail=1; }
  [[ "$fail" == 0 ]] && echo "==> check OK: all sizes render cleanly from the SVG" || exit 1
else
  echo "==> done: $OUT/excise_logo_{16,32,64,128,256}.png + excise_logo.ico"
fi
