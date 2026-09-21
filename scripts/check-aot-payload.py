#!/usr/bin/env python3
"""Fail unless a directory of shipped files is a Native AOT payload.

The release workflow (.github/workflows/release.yml) runs this on what each
platform's package actually contains. A Native AOT publish is a native executable
plus native libraries; the two ways a publish silently falls back to something
else both leave evidence this script reads:

  1. A managed assembly. Every managed .dll (self-contained, ReadyToRun,
     framework-dependent) is a PE file with a non-empty CLI header, data directory
     14 of the optional header (ECMA-335 II.25.3.3). This is judged from the BYTES,
     not the extension: a Windows AOT payload legitimately carries native .dll
     files (libSkiaSharp.dll, libHarfBuzzSharp.dll, av_libglesv2.dll), so counting
     `*.dll` cannot work there, and macOS/Linux managed assemblies are still PE
     files named .dll. ELF and Mach-O files are not PE and are skipped.
  2. A single-file bundle, which is what the Windows package was before this check.
     A bundle's outer executable is a native apphost with the assemblies appended,
     so it has NO CLI header and rule 1 cannot see it. The apphost template embeds
     the 32-byte bundle marker (SHA-256 of ".net core bundle", dotnet/runtime
     HostWriter); an AOT-linked executable does not contain it. Each --main
     executable is scanned for it.

Also refused: the runtime itself (coreclr, clrjit, hostfxr, hostpolicy,
System.Private.CoreLib) among the files.

The verdict is only worth something if it can fail, so `--self-test` builds
synthetic payloads (a native PE, PE32 and PE32+ managed images, a bundle-marked
apphost, an empty tree, ...) and asserts this script rejects each bad one and
accepts the good one. The workflow runs it before the real check.

Usage:
  scripts/check-aot-payload.py [--main NAME]... DIR
  scripts/check-aot-payload.py --self-test

  --main NAME  a file that must exist somewhere under DIR (matched by exact base
               name) and must not carry the single-file bundle marker. Repeatable.

Exit: 0 clean, 1 not an AOT payload (or self-test failed), 2 usage error.
"""

import argparse
import os
import shutil
import struct
import sys
import tempfile

# SHA-256(".net core bundle"), the marker the apphost carries so the bundler can
# find its header-offset slot (8 zero bytes precede it in an unbundled apphost; a
# bundle rewrites the 8 bytes and leaves the marker). Verified against the
# osx-arm64 apphost of SDK 10.0.12 at offset 81928.
BUNDLE_MARKER = bytes.fromhex(
    "8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae"
)

# Files that exist only when the CLR travels with the app. Lower case, full base
# name, because a Native AOT publish contains none of them on any platform.
RUNTIME_FILES = frozenset(
    [
        "coreclr.dll", "libcoreclr.so", "libcoreclr.dylib",
        "clrjit.dll", "libclrjit.so", "libclrjit.dylib",
        "hostfxr.dll", "libhostfxr.so", "libhostfxr.dylib",
        "hostpolicy.dll", "libhostpolicy.so", "libhostpolicy.dylib",
        "system.private.corelib.dll",
    ]
)

CLI_HEADER_DIRECTORY = 14  # IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR
MAX_REPORTED = 20

PE_NOT_PE = "not-pe"
PE_NATIVE = "native"
PE_MANAGED = "managed"


def classify_pe(path):
    """Return PE_NOT_PE, PE_NATIVE or PE_MANAGED from the file's headers alone."""
    with open(path, "rb") as f:
        dos = f.read(64)
        if len(dos) < 64 or dos[:2] != b"MZ":
            return PE_NOT_PE
        (e_lfanew,) = struct.unpack_from("<I", dos, 0x3C)
        f.seek(e_lfanew)
        coff = f.read(24)
        if len(coff) < 24 or coff[:4] != b"PE\0\0":
            return PE_NOT_PE
        (size_of_optional,) = struct.unpack_from("<H", coff, 20)
        opt = f.read(min(size_of_optional, 256))
    if len(opt) < 2:
        return PE_NATIVE
    (magic,) = struct.unpack_from("<H", opt, 0)
    if magic == 0x10B:  # PE32
        count_at, dirs_at = 92, 96
    elif magic == 0x20B:  # PE32+
        count_at, dirs_at = 108, 112
    else:
        return PE_NATIVE
    if len(opt) < count_at + 4:
        return PE_NATIVE
    (count,) = struct.unpack_from("<I", opt, count_at)
    entry_at = dirs_at + CLI_HEADER_DIRECTORY * 8
    if count <= CLI_HEADER_DIRECTORY or len(opt) < entry_at + 8:
        return PE_NATIVE
    rva, size = struct.unpack_from("<II", opt, entry_at)
    return PE_MANAGED if (rva or size) else PE_NATIVE


def contains(path, needle, chunk=1 << 20):
    """True when `needle` occurs in the file. Streams, and keeps an overlap so a
    match straddling two reads is still found."""
    keep = len(needle) - 1
    tail = b""
    with open(path, "rb") as f:
        while True:
            block = f.read(chunk)
            if not block:
                return False
            buf = tail + block
            if needle in buf:
                return True
            tail = buf[-keep:] if keep else b""


def check_tree(root, mains, chunk=1 << 20):
    """Return (findings, stats). `findings` is empty for an AOT payload."""
    findings = []
    stats = {"files": 0, "pe": 0, "managed": 0}
    if not os.path.isdir(root):
        return [f"not a directory: {root}"], stats

    found_mains = {name: [] for name in mains}
    for dirpath, _dirs, names in os.walk(root):
        for name in sorted(names):
            path = os.path.join(dirpath, name)
            # A symlink (the .deb's /usr/bin/excise) is a pointer, not shipped bytes.
            if os.path.islink(path) or not os.path.isfile(path):
                continue
            rel = os.path.relpath(path, root)
            stats["files"] += 1

            if name.lower() in RUNTIME_FILES:
                findings.append(f"{rel}: the .NET runtime ships with the app, so this is not Native AOT")

            kind = classify_pe(path)
            if kind != PE_NOT_PE:
                stats["pe"] += 1
            if kind == PE_MANAGED:
                stats["managed"] += 1
                findings.append(f"{rel}: managed assembly (PE with a CLI header)")

            if name in found_mains:
                found_mains[name].append((path, rel))

    if stats["files"] == 0:
        findings.append(f"{root}: no files, so nothing was verified")

    for name, hits in found_mains.items():
        if not hits:
            findings.append(f"{name}: expected executable not found under {root}")
        for path, rel in hits:
            if contains(path, BUNDLE_MARKER, chunk):
                findings.append(
                    f"{rel}: carries the .NET apphost bundle marker, i.e. a single-file or "
                    "plain self-contained build and not a Native AOT executable"
                )
    return findings, stats


# ---------------------------------------------------------------------------
# --self-test: a checker nobody has watched fail is a checker that cannot fail.
# ---------------------------------------------------------------------------

def make_pe(managed, pe32_plus=True):
    """A minimal PE image: DOS header, PE signature, COFF header, optional header
    with 16 data directories, of which directory 14 is set only when `managed`."""
    optional_size = 240 if pe32_plus else 224
    count_at, dirs_at = (108, 112) if pe32_plus else (92, 96)
    dos = bytearray(64)
    dos[0:2] = b"MZ"
    struct.pack_into("<I", dos, 0x3C, 0x80)
    coff = b"PE\0\0" + struct.pack("<HHIIIHH", 0x8664, 0, 0, 0, 0, optional_size, 0x2022)
    opt = bytearray(optional_size)
    struct.pack_into("<H", opt, 0, 0x20B if pe32_plus else 0x10B)
    struct.pack_into("<I", opt, count_at, 16)
    if managed:
        struct.pack_into("<II", opt, dirs_at + CLI_HEADER_DIRECTORY * 8, 0x2008, 0x48)
    return bytes(dos) + b"\0" * (0x80 - 64) + coff + bytes(opt) + b"\0" * 64


def self_test():
    failures = []
    root = tempfile.mkdtemp(prefix="check-aot-payload-selftest-")

    def tree(name, files):
        d = os.path.join(root, name)
        os.makedirs(d)
        for rel, data in files.items():
            p = os.path.join(d, rel)
            os.makedirs(os.path.dirname(p), exist_ok=True)
            with open(p, "wb") as f:
                f.write(data)
        return d

    native = make_pe(False)
    native32 = make_pe(False, pe32_plus=False)
    managed = make_pe(True)
    managed32 = make_pe(True, pe32_plus=False)
    apphost = b"\x7fELF" + b"\0" * 5000 + b"\0" * 8 + BUNDLE_MARKER + b"\0" * 5000
    aot_exe = b"\x7fELF" + os.urandom(6000)
    elf_lib = b"\x7fELF" + os.urandom(2000)

    def expect(label, directory, mains, want_ok, must_mention=None, chunk=1 << 20):
        findings, _ = check_tree(directory, mains, chunk)
        ok = not findings
        good = ok == want_ok and (
            must_mention is None or any(must_mention in f for f in findings)
        )
        print(f"  {'PASS' if good else 'FAIL'}  {label}")
        if not good:
            failures.append(f"{label}: findings={findings}")

    try:
        expect(
            "an AOT-shaped payload (native exe, native PE dlls, ELF lib, docs) is accepted",
            tree("good", {
                "Excise.App.exe": native, "libSkiaSharp.dll": native32,
                "av_libglesv2.dll": native, "libx.so": elf_lib,
                "README.md": b"hello", "empty.bin": b"", "one.bin": b"M",
                "sub/notes.txt": b"MZ but not a PE",
            }),
            ["Excise.App.exe"], True)
        expect(
            "a PE32+ managed dll is rejected",
            tree("m64", {"Excise.App.exe": native, "Excise.Core.dll": managed}),
            ["Excise.App.exe"], False, "Excise.Core.dll")
        expect(
            "a PE32 managed dll is rejected",
            tree("m32", {"Excise.App.exe": native, "Old.dll": managed32}),
            ["Excise.App.exe"], False, "Old.dll")
        expect(
            "a managed assembly is caught by its bytes when renamed to .bin",
            tree("renamed", {"Excise.App.exe": native, "data.bin": managed}),
            ["Excise.App.exe"], False, "data.bin")
        expect(
            "a managed main executable is rejected",
            tree("mainmanaged", {"Excise.App.exe": managed}),
            ["Excise.App.exe"], False, "Excise.App.exe")
        expect(
            "the runtime (coreclr) among the files is rejected even when native",
            tree("coreclr", {"Excise.App.exe": native, "coreclr.dll": native}),
            ["Excise.App.exe"], False, "coreclr.dll")
        expect(
            "a single-file apphost (native, no CLI header) is rejected by its bundle marker",
            tree("bundle", {"Excise.App.exe": make_pe(False) + apphost}),
            ["Excise.App.exe"], False, "bundle marker")
        expect(
            "the bundle marker is found across a read boundary",
            tree("straddle", {"Excise.App": b"\0" * 10 + BUNDLE_MARKER + b"\0" * 10}),
            ["Excise.App"], False, "bundle marker", chunk=7)
        expect(
            "an AOT executable without the marker is accepted (Mach-O/ELF, no PE)",
            tree("aotexe", {"Excise.App": aot_exe, "excise": aot_exe}),
            ["Excise.App", "excise"], True)
        expect(
            "a required executable that is missing is rejected",
            tree("nomain", {"libSkiaSharp.dll": native}),
            ["Excise.App.exe"], False, "not found")
        expect(
            "an empty payload is rejected, not vacuously accepted",
            tree("empty", {}), [], False, "no files")
        expect(
            "a nonexistent directory is rejected",
            os.path.join(root, "does-not-exist"), [], False, "not a directory")
        expect(
            "an MZ file whose PE offset is past the end does not crash and is not a finding",
            tree("trunc", {"Excise.App.exe": native, "odd.dll": b"MZ" + b"\0" * 58 + struct.pack("<I", 1 << 30)}),
            ["Excise.App.exe"], True)
    finally:
        shutil.rmtree(root, ignore_errors=True)

    if failures:
        print("SELF-TEST FAILED:", file=sys.stderr)
        for line in failures:
            print(f"  {line}", file=sys.stderr)
        return 1
    print("self-test passed: the check accepts an AOT payload and rejects each fallback")
    return 0


def main(argv):
    parser = argparse.ArgumentParser(
        description="Fail unless a directory is a Native AOT payload.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument("directory", nargs="?")
    parser.add_argument("--main", action="append", default=[], metavar="NAME")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test()
    if not args.directory:
        parser.error("a directory is required (or --self-test)")

    findings, stats = check_tree(args.directory, args.main)
    print(
        f"{args.directory}: {stats['files']} files, {stats['pe']} PE images, "
        f"{stats['managed']} managed"
        + (f", executables checked: {', '.join(args.main)}" if args.main else "")
    )
    if findings:
        # A fallback build has hundreds of managed assemblies; the first few name
        # the problem and the count says how big it is.
        for line in findings[:MAX_REPORTED]:
            print(f"::error::not a Native AOT payload: {line}", file=sys.stderr)
        if len(findings) > MAX_REPORTED:
            print(
                f"::error::... and {len(findings) - MAX_REPORTED} more findings "
                f"({len(findings)} in total)",
                file=sys.stderr,
            )
        return 1
    print("  Native AOT payload: no managed assemblies, no runtime, no single-file bundle marker")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
