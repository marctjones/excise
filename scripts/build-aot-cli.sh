#!/usr/bin/env bash
# Publish the excise CLI with Native AOT and report what the binary actually links.
#
# WHY THIS IS A SCRIPT (#1389/#1390)
# ----------------------------------
# Native AOT of the CLI needs no special environment IF the SDK's NativeAOT runtime pack is
# the portable one Microsoft ships. Not every SDK on this machine is.
#
# THE HOMEBREW SDK IS "NONPORTABLE". Homebrew builds .NET from source against system
# libraries, so its runtime pack omits the vendored static libs (libz.a, libbrotli*.a) and
# carries a `nonportable.txt` sentinel. The ILC targets key off exactly those two facts
# (Microsoft.NETCore.Native.Unix.targets: `UseSystemBrotli` when libbrotlicommon.a is
# absent, `-lssl -lcrypto` when nonportable.txt exists), so the link line gains
# `-lssl -lcrypto -lz -lbrotli*` and the publish fails with `ld: library 'ssl' not found`
# unless LIBRARY_PATH points at Homebrew.
#
# That is not a cosmetic difference. Measured 2026-09-06:
#   * Homebrew SDK  -> binary dynamically links Homebrew openssl@3 + brotli (dies elsewhere),
#                      and links Apple's system zlib instead of the vendored zlib-ng, so it
#                      writes DIFFERENT PDF bytes than the JIT build (193507 vs 193455).
#   * Official SDK  -> fully self-contained (only /usr/lib and Apple frameworks), and writes
#                      BYTE-IDENTICAL PDF output to the JIT build.
# Nothing actually uses OpenSSL: zero symbols bind to libssl/libcrypto at runtime (TLS on
# macOS goes through Network.framework / Security.framework). It is a dead link-time flag.
#
# So this script PREFERS a portable SDK and only falls back to the Homebrew recipe, loudly.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT" || exit 1

RID=""
OUTPUT="$ROOT/artifacts/aot-cli"
QUIET=0

usage() {
    cat <<'EOF'
usage: scripts/build-aot-cli.sh [options]

Publish Excise.Cli with Native AOT.

  --rid <rid>       Runtime identifier (default: this machine's)
  --output <dir>    Publish directory (default: artifacts/aot-cli)
  --quiet           Print only the resulting binary path on success
  -h, --help        This message

On success the last line of stdout is the absolute path to the binary, so callers can do:
  EXCISE_BENCHMARK_CLI_COMMAND="$(scripts/build-aot-cli.sh --quiet)"
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --rid) RID="$2"; shift 2 ;;
        --output) OUTPUT="$2"; shift 2 ;;
        --quiet) QUIET=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

say() { [ "$QUIET" -eq 1 ] || echo "$@"; }

if [ -z "$RID" ]; then
    case "$(uname -s)/$(uname -m)" in
        Darwin/arm64) RID="osx-arm64" ;;
        Darwin/x86_64) RID="osx-x64" ;;
        Linux/aarch64) RID="linux-arm64" ;;
        Linux/x86_64) RID="linux-x64" ;;
        *) echo "cannot infer a RID for $(uname -s)/$(uname -m); pass --rid" >&2; exit 2 ;;
    esac
fi

# The repo's global.json pins the SDK band and ~/.zprofile points at the official install,
# so the dotnet on PATH is the right one. VERIFY rather than assume: a nonportable pack
# (Homebrew-style) silently changes what the binary links and what bytes it writes, and the
# publish would either fail or succeed misleadingly. `nonportable.txt` in a bundled pack is
# the exact sentinel Microsoft.NETCore.Native.Unix.targets tests.
DOTNET="${DOTNET:-$(command -v dotnet)}"
[ -n "$DOTNET" ] || { echo "no dotnet on PATH" >&2; exit 3; }

dotnet_root="$(cd "$(dirname "$DOTNET")" && pwd)"
[ -d "$dotnet_root/libexec/packs" ] && dotnet_root="$dotnet_root/libexec"
if find "$dotnet_root/packs" -path "*NativeAOT*/native/nonportable.txt" 2>/dev/null | grep -q .; then
    say "==> WARNING: $DOTNET has a NONPORTABLE NativeAOT pack (this is how Homebrew builds .NET)."
    say "    The binary will dynamically link system openssl@3/brotli and will use the system"
    say "    zlib instead of the vendored zlib-ng, so it writes DIFFERENT PDF bytes than the"
    say "    JIT build. Install the official SDK (https://dot.net) and put it first on PATH."
    if [ "$(uname -s)" = "Darwin" ] && command -v brew >/dev/null 2>&1; then
        LIB_DIRS=""
        for formula in openssl@3 brotli; do
            prefix="$(brew --prefix "$formula" 2>/dev/null)"
            [ -n "$prefix" ] && [ -d "$prefix/lib" ] && LIB_DIRS="${LIB_DIRS:+$LIB_DIRS:}$prefix/lib"
        done
        [ -n "$LIB_DIRS" ] && { export LIBRARY_PATH="${LIBRARY_PATH:+$LIBRARY_PATH:}$LIB_DIRS:$(brew --prefix)/lib"
                                say "    falling back to LIBRARY_PATH=$LIBRARY_PATH"; }
    fi
else
    say "==> using $DOTNET ($("$DOTNET" --version 2>/dev/null); portable NativeAOT pack)"
fi

say "==> publishing Excise.Cli (Native AOT, $RID) -> $OUTPUT"
build_log="$(mktemp -t excise-aot-cli)"
if ! "$DOTNET" publish "$ROOT/Excise.Cli/Excise.Cli.csproj" \
        -c Release -r "$RID" --self-contained true \
        -p:PublishAot=true -p:PublishReadyToRun=false -p:PublishSingleFile=false \
        -o "$OUTPUT" > "$build_log" 2>&1; then
    echo "AOT publish FAILED. Last 30 lines:" >&2
    tail -30 "$build_log" >&2
    exit 1
fi

BINARY="$OUTPUT/excise"
[ -f "$BINARY" ] || BINARY="$OUTPUT/excise.exe"
if [ ! -f "$BINARY" ]; then
    echo "publish reported success but no binary at $OUTPUT/excise — layout changed?" >&2
    exit 1
fi

# The AOT analyzers run on every build now (IsAotCompatible in the csproj), so any
# IL2026/IL3050 here is a NEW reflection site, not background noise.
# IL2026 (RequiresUnreferencedCode) and IL3050 (RequiresDynamicCode) are the two #1389
# is about: a reflection-based serializer call that would emit `{}` at runtime. Scope the
# check to those. IL2104 is a per-ASSEMBLY notice from a third-party dependency (CSJ2K,
# the JPEG 2000 decoder) and fires on every clean build — folding it in here would make
# this warn every time, which is how a check stops being read.
reflection_warnings="$(grep -cE "warning (IL2026|IL3050)" "$build_log" || true)"
if [ "${reflection_warnings:-0}" -gt 0 ]; then
    echo "==> FAIL: $reflection_warnings IL2026/IL3050 warning(s) — a reflection site has been" >&2
    echo "    reintroduced (#1389). Under AOT that surface emits an empty object at runtime." >&2
    grep -E "warning (IL2026|IL3050)" "$build_log" | sort -u | head -10 >&2
    rm -f "$build_log"
    exit 1
fi
third_party="$(grep -cE "warning IL2104" "$build_log" || true)"
[ "${third_party:-0}" -gt 0 ] && say "==> note: $third_party IL2104 third-party assembly notice(s) (CSJ2K) — expected, not ours"

if [ "$QUIET" -eq 0 ]; then
    echo "==> size: $(ls -l "$BINARY" | awk '{print $5}') bytes"
    # Report what the binary actually links rather than asserting either way: whether it
    # is self-contained depends on which SDK was selected above, and getting this wrong in
    # either direction misleads the ship decision on #1390.
    external=""
    if command -v otool >/dev/null 2>&1; then
        external="$(otool -L "$BINARY" | tail -n +2 | grep -vE "/usr/lib/|/System/" || true)"
    elif command -v ldd >/dev/null 2>&1; then
        external="$(ldd "$BINARY" | grep -vE "/lib/|/usr/lib/|linux-vdso|ld-linux" || true)"
    fi
    if [ -n "$external" ]; then
        echo "==> NOT self-contained — dynamically links, and will not run without:"
        echo "$external" | sed 's/^/      /'
        echo "    It also links the system zlib rather than the vendored zlib-ng, so it writes"
        echo "    different PDF bytes than the JIT build. Prefer a portable SDK (see the header)."
    else
        echo "==> self-contained: nothing outside /usr/lib and Apple frameworks."
    fi
    printf '==> smoke: '
    if "$BINARY" --version >/dev/null 2>&1; then echo "--version OK"; else echo "--version FAILED"; rm -f "$build_log"; exit 1; fi
fi

rm -f "$build_log"
echo "$BINARY"
