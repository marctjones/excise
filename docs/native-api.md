# Native C API (`Excise.Native`)

A NativeAOT shared library with a stable C ABI over `Excise.Core`, for callers that are not
.NET: C, Python, Swift, Rust, Go. The contract is [`Excise.Native/include/excise.h`](../Excise.Native/include/excise.h);
every function is documented there.

**Included:** open (bytes or path, with password), page count and size, text extraction,
glyph-level text redaction, area redaction, save, encrypt, decrypt.
**Not included:** rendering. It needs SkiaSharp and HarfBuzz natives and will be a separate library.

## Build

```bash
scripts/build-native-lib.sh [--rid osx-arm64]   # -> dist/native/<rid>/{libexcise_native.*, include/excise.h}
scripts/test-native-lib.sh                      # build, then the C and Python tests
```

NativeAOT does not cross-compile: build each RID on its own OS. Use Microsoft's official
.NET 10 SDK (the script refuses Homebrew's). Library names: `libexcise_native.dylib`,
`libexcise_native.so`, `excise_native.dll`.

## Usage

C:

```c
#include "excise.h"

excise_doc* doc = NULL;
if (excise_open_path("in.pdf", NULL, &doc) != EXCISE_OK) { fputs(excise_last_error(), stderr); return 1; }

int32_t removed = 0;
int rc = excise_redact_text(doc, "SECRET", &removed);   /* 0 only if the term is really gone */
if (rc == EXCISE_OK) rc = excise_save_path(doc, "out.pdf");
if (rc != EXCISE_OK) fputs(excise_last_error(), stderr);
excise_close(doc);
```

Python (`ctypes`): see `Excise.Native/tests/test_ctypes.py` for the full set of signatures.

```python
lib = ctypes.CDLL("dist/native/osx-arm64/libexcise_native.dylib")
lib.excise_open_path.argtypes = [c_char_p, c_char_p, POINTER(c_void_p)]
doc = c_void_p()
assert lib.excise_open_path(b"in.pdf", None, byref(doc)) == 0
```

## Error model

Every function returns `int`: `0` (`EXCISE_OK`) or an `EXCISE_ERR_*` code (see the enum in the header).
Results come back through out-parameters, which are zeroed first. Nothing throws or aborts across
the boundary. After a failure `excise_last_error()` returns a UTF-8 message for the calling thread.

`EXCISE_ERR_REDACTION_INCOMPLETE` is the one to handle deliberately: the engine located something
it could not remove (a term split across lines, a carrier it refused to scrub). The document was
modified but must not be treated as redacted. `*out_removed_count` is still set.

Redaction uses the same defaults as `excise redact` (Standard profile, case-insensitive,
covering box). Verify a redacted file with an independent tool (`mutool draw -F txt`, `pdftotext`);
`ExtractText` of this library is not an oracle for its own redaction.

## Memory ownership

Callers own every buffer they pass in; the library copies what it needs and keeps nothing after a
call returns. Buffers the library hands out (`excise_extract_text`, `excise_save_bytes`) are
NUL-terminated (the NUL is not counted in the length) and are released with `excise_free_buffer`.
`excise_version()` and `excise_last_error()` return library-owned pointers: do not free them.

## Threads and handles

A handle may be used from one thread at a time; concurrent use returns `EXCISE_ERR_BUSY`.
Different handles are independent. `excise_last_error()` is per thread.
Handles are opaque, never reused, and validated on every call: a closed, stale or garbage handle
returns `EXCISE_ERR_INVALID_HANDLE`.

## Encryption

`excise_save_*` on a document opened with a password re-encrypts with the same password and
permissions. `excise_decrypt_save_path` writes an unprotected copy. `excise_encrypt_save_path`
writes AES-256 with all permissions granted.

## Limits

No timeout or cancellation: a pathological PDF can run long. Run untrusted input in a process
you can kill.
