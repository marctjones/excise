/*
 * excise.h - C ABI of the excise PDF engine (Excise.Core, NativeAOT shared library).
 *
 * Scope: open, page info, text extraction, glyph-level redaction, save, encrypt,
 * decrypt. Rendering is NOT part of this library.
 *
 * Conventions
 *  - Every function that can fail returns int: 0 (EXCISE_OK) or an EXCISE_ERR_* code.
 *    Results come back through out-parameters, which are set to zero/NULL first.
 *  - Nothing ever throws or aborts across this boundary. After a failure,
 *    excise_last_error() describes it (UTF-8, per calling thread).
 *  - All strings are UTF-8, NUL-terminated. Callers own every buffer they pass in; the
 *    library keeps nothing after a call returns. Buffers the library hands out
 *    (out_utf8, out data) are NUL-terminated (the NUL is not counted in *out_len)
 *    and must be released with excise_free_buffer().
 *  - Pages are numbered from 1. Page coordinates are PDF user space: origin at the
 *    bottom-left of the page, y up, in points (1/72 inch).
 *
 * Thread safety
 *  - A document handle may be used from one thread at a time. Concurrent use of the
 *    same handle returns EXCISE_ERR_BUSY. Different handles are fully independent and
 *    may be used from different threads at once.
 *  - excise_last_error() is per calling thread.
 *
 * Handles are opaque and never reused: passing a closed, stale or garbage handle
 * returns EXCISE_ERR_INVALID_HANDLE.
 */
#ifndef EXCISE_H
#define EXCISE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  define EXCISE_CALL __cdecl
#else
#  define EXCISE_CALL
#endif

/* Bumped on any incompatible change to this header. Compare with excise_abi_version(). */
#define EXCISE_ABI_VERSION 1

/* Opaque document handle. */
typedef struct excise_doc excise_doc;

typedef enum excise_status {
    EXCISE_OK = 0,
    EXCISE_ERR_INVALID_ARG = 1,         /* NULL pointer, empty term, non-finite number, ... */
    EXCISE_ERR_BAD_PDF = 2,             /* not a PDF, or too damaged to read */
    EXCISE_ERR_PASSWORD = 3,            /* encrypted and the password is missing or wrong */
    EXCISE_ERR_IO = 4,                  /* file could not be read or written */
    EXCISE_ERR_UNSUPPORTED = 5,         /* e.g. certificate (public-key) encryption */
    EXCISE_ERR_INVALID_HANDLE = 6,      /* unknown, closed or garbage handle */
    EXCISE_ERR_PAGE_RANGE = 7,          /* page number outside 1..page_count */
    EXCISE_ERR_REDACTION_REFUSED = 8,   /* the engine refused before changing anything */
    EXCISE_ERR_BUSY = 9,                /* handle in use by another thread */
    EXCISE_ERR_REDACTION_INCOMPLETE = 10, /* redaction ran but was NOT clean; see below */
    EXCISE_ERR_INTERNAL = 99            /* unexpected failure inside the library */
} excise_status;

/* Library version string, e.g. "excise-native 1". Static; do not free. */
const char* EXCISE_CALL excise_version(void);

/* ABI version of the loaded library; must equal EXCISE_ABI_VERSION. */
int EXCISE_CALL excise_abi_version(void);

/*
 * UTF-8 message describing the last failure on the calling thread, or "" if none.
 * The pointer is owned by the library and valid until the next failing call on the
 * same thread. Do not free it.
 */
const char* EXCISE_CALL excise_last_error(void);

/* Release a buffer returned by this library (out_utf8, out data). NULL is ignored. */
void EXCISE_CALL excise_free_buffer(void* buffer);

/*
 * Open a PDF from memory (the bytes are copied; `data` may be freed on return).
 * password_utf8: user password, or NULL for none/empty.
 * On success *out is a new handle to close with excise_close().
 */
int EXCISE_CALL excise_open_bytes(const uint8_t* data, size_t len,
                                  const char* password_utf8, excise_doc** out);

/* Open a PDF from a file (fully read into memory; the file is not held open). */
int EXCISE_CALL excise_open_path(const char* path_utf8,
                                 const char* password_utf8, excise_doc** out);

/* Close a handle. Closing twice returns EXCISE_ERR_INVALID_HANDLE. */
int EXCISE_CALL excise_close(excise_doc* doc);

/* Number of pages. */
int EXCISE_CALL excise_page_count(excise_doc* doc, int32_t* out_count);

/* Page size in points (MediaBox width and height). `page` is 1-based. */
int EXCISE_CALL excise_page_size(excise_doc* doc, int32_t page,
                                 double* out_width, double* out_height);

/* Extract the text of one page as UTF-8. Free *out_utf8 with excise_free_buffer(). */
int EXCISE_CALL excise_extract_text(excise_doc* doc, int32_t page,
                                    uint8_t** out_utf8, size_t* out_len);

/*
 * Redact every occurrence of `term_utf8` in the document, removing the glyphs from the
 * PDF structure (not just covering them) and scrubbing document carriers (metadata,
 * outlines, annotations, attachments, ...). Same defaults as `excise redact`: Standard
 * profile, case-insensitive, covering box drawn.
 *
 * *out_removed_count is the number of occurrences verified gone, and is set even when
 * the function fails with EXCISE_ERR_REDACTION_INCOMPLETE.
 *
 * EXCISE_ERR_REDACTION_INCOMPLETE means the engine located something it could not
 * remove (or a carrier it could not scrub, or a term split across lines). The document
 * is modified but must NOT be treated as redacted; excise_last_error() has details.
 */
int EXCISE_CALL excise_redact_text(excise_doc* doc, const char* term_utf8,
                                   int32_t* out_removed_count);

/*
 * Redact everything inside a rectangle on one page (text, vector graphics, images).
 * Coordinates are PDF user space (bottom-left origin). Same defaults as the CLI.
 */
int EXCISE_CALL excise_redact_area(excise_doc* doc, int32_t page,
                                   double left, double bottom, double right, double top);

/*
 * Save to a file. A document opened encrypted is saved re-encrypted with the same
 * password and permissions; use excise_decrypt_save_path() to write it unprotected.
 * Saving to the path the document was opened from is allowed.
 */
int EXCISE_CALL excise_save_path(excise_doc* doc, const char* path_utf8);

/* Save to memory (same encryption rule). Free *out_data with excise_free_buffer(). */
int EXCISE_CALL excise_save_bytes(excise_doc* doc, uint8_t** out_data, size_t* out_len);

/*
 * Save encrypted with AES-256 (PDF 2.0 handler), all permissions granted. At least one
 * of the two passwords must be non-empty; NULL means none.
 */
int EXCISE_CALL excise_encrypt_save_path(excise_doc* doc, const char* path_utf8,
                                         const char* user_password_utf8,
                                         const char* owner_password_utf8);

/* Save WITHOUT encryption, even if the source was encrypted (it was opened with its password). */
int EXCISE_CALL excise_decrypt_save_path(excise_doc* doc, const char* path_utf8);

#ifdef __cplusplus
}
#endif

#endif /* EXCISE_H */
