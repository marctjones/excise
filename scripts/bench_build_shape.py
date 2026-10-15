#!/usr/bin/env python3
"""Which build shape is this excise bundle? NativeAOT, ReadyToRun or plain IL (JIT).

Every number a benchmark prints depends on this: NativeAOT starts about 240 ms sooner, rests 80 MB
lower and uses 55-70% less CPU than ReadyToRun on the same commit (2026-10-15), so a figure with no
shape beside it cannot be compared with another. The reader, speed and scenario harnesses call this
for the bundle they measure, print the answer and write it into their summaries.

    scripts/bench_build_shape.py logs/reader-bench-bundle-aot/excise.app
    from bench_build_shape import detect, banner

Detection, from the bundle alone (no running it):
  * NativeAOT: Contents/MacOS/Excise.App is one large native binary and there is no managed
    Excise.Core.dll beside it.
  * ReadyToRun: Excise.Core.dll carries a ManagedNativeHeader (PE CLI header, offset 0x40).
  * IL (JIT): Excise.Core.dll exists and has no ManagedNativeHeader (an ordinary `dotnet build`).
"""
import pathlib
import struct
import sys

AOT = "NativeAOT"
R2R = "ReadyToRun"
IL = "IL (JIT)"
UNKNOWN = "unknown"

# A NativeAOT Excise.App with the viewer, renderer and OCR compiled in is ~45 MB; the ReadyToRun
# and IL apphosts are well under 1 MB. 10 MB separates them with room on both sides.
_NATIVE_MIN_BYTES = 10 * 1024 * 1024


def _has_ready_to_run_header(dll: pathlib.Path) -> bool:
    d = dll.read_bytes()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", d, opt)[0]
    data_dirs = opt + (112 if magic == 0x20B else 96)
    cli_rva = struct.unpack_from("<I", d, data_dirs + 14 * 8)[0]
    n_sections = struct.unpack_from("<H", d, pe + 6)[0]
    sections = opt + struct.unpack_from("<H", d, pe + 20)[0]
    cli_off = None
    for i in range(n_sections):
        s = sections + i * 40
        vsize, va, rsize, raw = struct.unpack_from("<IIII", d, s + 8)
        if va <= cli_rva < va + max(vsize, rsize):
            cli_off = raw + cli_rva - va
            break
    if cli_off is None:
        return False
    managed_native_header_rva = struct.unpack_from("<I", d, cli_off + 0x40)[0]
    return managed_native_header_rva != 0


def _bundle_mb(app: pathlib.Path) -> float:
    return round(sum(f.stat().st_size for f in app.rglob("*") if f.is_file()) / 2**20, 1)


def detect(app_path) -> dict:
    """{'shape': ..., 'evidence': ..., 'bundleMB': ...} for an excise.app (or its Contents/MacOS directory)."""
    r = _detect(app_path)
    app = pathlib.Path(app_path)
    r["bundleMB"] = _bundle_mb(app) if app.exists() else None
    return r


def _detect(app_path) -> dict:
    app = pathlib.Path(app_path)
    macos = app / "Contents" / "MacOS" if (app / "Contents").exists() else app
    exe = macos / "Excise.App"
    core = macos / "Excise.Core.dll"
    if not exe.exists():
        return {"shape": UNKNOWN, "evidence": f"no Excise.App binary under {macos}"}
    if not core.exists():
        if exe.stat().st_size >= _NATIVE_MIN_BYTES:
            mb = exe.stat().st_size / 2**20
            return {"shape": AOT, "evidence": f"single native Excise.App ({mb:.0f} MB), no managed Excise.Core.dll"}
        return {"shape": UNKNOWN, "evidence": "no Excise.Core.dll and a small Excise.App"}
    if _has_ready_to_run_header(core):
        return {"shape": R2R, "evidence": "Excise.Core.dll has a ReadyToRun ManagedNativeHeader"}
    return {"shape": IL, "evidence": "Excise.Core.dll is IL only (no ReadyToRun header)"}


def banner(app_path, label="excise") -> str:
    """A one-line statement for logs, and a warning line when the shape is not NativeAOT."""
    r = detect(app_path)
    size = f", bundle {r['bundleMB']} MB" if r.get("bundleMB") else ""
    line = f"{label} build shape: {r['shape']} ({r['evidence']}{size}) [{app_path}]"
    if r["shape"] != AOT:
        line += (f"\n  WARNING: this is not the NativeAOT shape. Numbers from it are not comparable with NativeAOT numbers "
                 f"(see scripts/bench_build_shape.py); build one with scripts/build-macos-app.sh --aot "
                 f"--output logs/reader-bench-bundle-aot")
    return line


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if a != "--shape"]
    if len(args) != 1:
        sys.exit("usage: bench_build_shape.py [--shape] <path to excise.app>")
    if "--shape" in sys.argv[1:]:   # machine form for shell callers: just the shape name
        print(detect(args[0])["shape"])
    else:
        print(banner(args[0]))
    sys.exit(0 if detect(args[0])["shape"] == AOT else 1)
