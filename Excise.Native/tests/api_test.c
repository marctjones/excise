/*
 * C test for the excise C ABI. Loads the library with dlopen, so it exercises the
 * shipped binary through excise.h types exactly as a C consumer would.
 *
 *   api_test <library> <input.pdf> <term-present-on-page-1> <scratch-dir>
 *
 * Prints "PASSED n" and exits 0, or names the failed check and exits 1.
 */
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "../include/excise.h"

static int g_checks = 0;
static int g_failed = 0;

#define CHECK(cond)                                                            \
    do {                                                                       \
        g_checks++;                                                            \
        if (!(cond)) {                                                         \
            g_failed++;                                                        \
            fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);    \
        }                                                                      \
    } while (0)

#define CHECK_EQ(actual, expected)                                             \
    do {                                                                       \
        long a_ = (long)(actual), e_ = (long)(expected);                       \
        g_checks++;                                                            \
        if (a_ != e_) {                                                        \
            g_failed++;                                                        \
            fprintf(stderr, "FAIL %s:%d: %s == %ld, expected %ld (%s)\n",      \
                    __FILE__, __LINE__, #actual, a_, e_, last());              \
        }                                                                      \
    } while (0)

static void* lib;
static int (*abi_version)(void);
static const char* (*version)(void);
static const char* (*last_error)(void);
static void (*free_buffer)(void*);
static int (*open_bytes)(const uint8_t*, size_t, const char*, excise_doc**);
static int (*open_path)(const char*, const char*, excise_doc**);
static int (*close_doc)(excise_doc*);
static int (*page_count)(excise_doc*, int32_t*);
static int (*page_size)(excise_doc*, int32_t, double*, double*);
static int (*extract_text)(excise_doc*, int32_t, uint8_t**, size_t*);
static int (*redact_text)(excise_doc*, const char*, int32_t*);
static int (*redact_area)(excise_doc*, int32_t, double, double, double, double);
static int (*save_path)(excise_doc*, const char*);
static int (*save_bytes)(excise_doc*, uint8_t**, size_t*);
static int (*encrypt_save_path)(excise_doc*, const char*, const char*, const char*);
static int (*decrypt_save_path)(excise_doc*, const char*);

static const char* last(void) { return last_error ? last_error() : ""; }

static void* sym(const char* name) {
    void* p = dlsym(lib, name);
    if (!p) { fprintf(stderr, "missing export %s\n", name); exit(1); }
    return p;
}

static uint8_t* read_file(const char* path, size_t* len) {
    FILE* f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long n = ftell(f);
    fseek(f, 0, SEEK_SET);
    uint8_t* buf = malloc((size_t)n + 1);
    if (fread(buf, 1, (size_t)n, f) != (size_t)n) { fclose(f); free(buf); return NULL; }
    fclose(f);
    *len = (size_t)n;
    return buf;
}

/* Returns a malloc'd copy of the page text, or NULL. */
static char* page_text(excise_doc* d, int page) {
    uint8_t* out = NULL;
    size_t len = 0;
    if (extract_text(d, page, &out, &len) != EXCISE_OK) return NULL;
    char* copy = malloc(len + 1);
    memcpy(copy, out, len);
    copy[len] = 0;
    free_buffer(out);
    return copy;
}

int main(int argc, char** argv) {
    if (argc < 5) {
        fprintf(stderr, "usage: api_test <library> <input.pdf> <term> <scratch-dir>\n");
        return 2;
    }
    const char* libpath = argv[1];
    const char* pdf = argv[2];
    const char* term = argv[3];
    const char* scratch = argv[4];
    char out_path[1024], enc_path[1024], dec_path[1024];
    snprintf(out_path, sizeof out_path, "%s/c-redacted.pdf", scratch);
    snprintf(enc_path, sizeof enc_path, "%s/c-encrypted.pdf", scratch);
    snprintf(dec_path, sizeof dec_path, "%s/c-decrypted.pdf", scratch);

    lib = dlopen(libpath, RTLD_NOW);
    if (!lib) { fprintf(stderr, "dlopen: %s\n", dlerror()); return 1; }
    abi_version = sym("excise_abi_version");
    version = sym("excise_version");
    last_error = sym("excise_last_error");
    free_buffer = sym("excise_free_buffer");
    open_bytes = sym("excise_open_bytes");
    open_path = sym("excise_open_path");
    close_doc = sym("excise_close");
    page_count = sym("excise_page_count");
    page_size = sym("excise_page_size");
    extract_text = sym("excise_extract_text");
    redact_text = sym("excise_redact_text");
    redact_area = sym("excise_redact_area");
    save_path = sym("excise_save_path");
    save_bytes = sym("excise_save_bytes");
    encrypt_save_path = sym("excise_encrypt_save_path");
    decrypt_save_path = sym("excise_decrypt_save_path");

    /* identity */
    CHECK_EQ(abi_version(), EXCISE_ABI_VERSION);
    CHECK(strstr(version(), "excise") != NULL);
    CHECK(last_error() != NULL);

    /* open from path, queries */
    excise_doc* d = NULL;
    CHECK_EQ(open_path(pdf, NULL, &d), EXCISE_OK);
    CHECK(d != NULL);
    int32_t pages = 0;
    CHECK_EQ(page_count(d, &pages), EXCISE_OK);
    CHECK(pages >= 1);
    double w = 0, h = 0;
    CHECK_EQ(page_size(d, 1, &w, &h), EXCISE_OK);
    CHECK(w > 100 && h > 100);

    char* before = page_text(d, 1);
    CHECK(before != NULL && strstr(before, term) != NULL);

    /* redact + save */
    int32_t removed = -1;
    CHECK_EQ(redact_text(d, term, &removed), EXCISE_OK);
    CHECK(removed >= 1);
    char* after = page_text(d, 1);
    CHECK(after != NULL && strstr(after, term) == NULL);
    CHECK_EQ(save_path(d, out_path), EXCISE_OK);

    uint8_t* bytes = NULL;
    size_t blen = 0;
    CHECK_EQ(save_bytes(d, &bytes, &blen), EXCISE_OK);
    CHECK(bytes != NULL && blen > 100 && memcmp(bytes, "%PDF-", 5) == 0);
    CHECK(bytes[blen] == 0); /* documented NUL terminator */

    /* the saved bytes reopen through open_bytes and no longer contain the term */
    excise_doc* d2 = NULL;
    CHECK_EQ(open_bytes(bytes, blen, NULL, &d2), EXCISE_OK);
    char* reread = page_text(d2, 1);
    CHECK(reread != NULL && strstr(reread, term) == NULL);
    free(reread);
    CHECK_EQ(close_doc(d2), EXCISE_OK);
    free_buffer(bytes);
    free(before);
    free(after);

    /* area redaction on an untouched copy */
    excise_doc* d3 = NULL;
    CHECK_EQ(open_path(pdf, NULL, &d3), EXCISE_OK);
    CHECK_EQ(redact_area(d3, 1, 0, 0, w, h), EXCISE_OK);
    char* blank = page_text(d3, 1);
    CHECK(blank != NULL && strstr(blank, term) == NULL);
    free(blank);
    CHECK_EQ(redact_area(d3, 1, 0.0 / 0.0, 0, 10, 10), EXCISE_ERR_INVALID_ARG);
    CHECK_EQ(redact_area(d3, 999, 0, 0, 10, 10), EXCISE_ERR_PAGE_RANGE);
    CHECK_EQ(close_doc(d3), EXCISE_OK);

    /* encrypt then open with / without / wrong password */
    CHECK_EQ(encrypt_save_path(d, enc_path, "s3cret", NULL), EXCISE_OK);
    excise_doc* e = NULL;
    CHECK_EQ(open_path(enc_path, NULL, &e), EXCISE_ERR_PASSWORD);
    CHECK(e == NULL);
    CHECK(strlen(last_error()) > 0);
    CHECK_EQ(open_path(enc_path, "wrong", &e), EXCISE_ERR_PASSWORD);
    CHECK_EQ(open_path(enc_path, "s3cret", &e), EXCISE_OK);
    CHECK(e != NULL);
    CHECK_EQ(decrypt_save_path(e, dec_path), EXCISE_OK);
    CHECK_EQ(close_doc(e), EXCISE_OK);
    excise_doc* dec = NULL;
    CHECK_EQ(open_path(dec_path, NULL, &dec), EXCISE_OK); /* no password needed now */
    CHECK_EQ(close_doc(dec), EXCISE_OK);
    CHECK_EQ(encrypt_save_path(d, enc_path, NULL, NULL), EXCISE_ERR_INVALID_ARG);

    /* argument errors */
    excise_doc* bad = (excise_doc*)0x1;
    static const uint8_t junk[] = "this is not a pdf at all";
    CHECK_EQ(open_bytes(junk, sizeof junk, NULL, &bad), EXCISE_ERR_BAD_PDF);
    CHECK(bad == NULL); /* out-param zeroed on failure */
    CHECK(strlen(last_error()) > 0);
    CHECK_EQ(open_bytes(NULL, 5, NULL, &bad), EXCISE_ERR_INVALID_ARG);
    CHECK_EQ(open_bytes(junk, sizeof junk, NULL, NULL), EXCISE_ERR_INVALID_ARG);
    CHECK_EQ(open_path(NULL, NULL, &bad), EXCISE_ERR_INVALID_ARG);
    CHECK_EQ(open_path("/nonexistent/dir/x.pdf", NULL, &bad), EXCISE_ERR_IO);
    CHECK_EQ(page_count(d, NULL), EXCISE_ERR_INVALID_ARG);
    CHECK_EQ(page_size(d, 1, NULL, &h), EXCISE_ERR_INVALID_ARG);
    CHECK_EQ(page_size(d, 0, &w, &h), EXCISE_ERR_PAGE_RANGE);
    CHECK_EQ(page_size(d, pages + 1, &w, &h), EXCISE_ERR_PAGE_RANGE);
    CHECK_EQ(page_size(d, -3, &w, &h), EXCISE_ERR_PAGE_RANGE);
    uint8_t* tp = NULL;
    size_t tl = 0;
    CHECK_EQ(extract_text(d, pages + 1, &tp, &tl), EXCISE_ERR_PAGE_RANGE);
    CHECK(tp == NULL && tl == 0);
    CHECK_EQ(extract_text(d, 1, NULL, &tl), EXCISE_ERR_INVALID_ARG);
    CHECK_EQ(redact_text(d, NULL, &removed), EXCISE_ERR_INVALID_ARG);
    CHECK_EQ(redact_text(d, "", &removed), EXCISE_ERR_INVALID_ARG);
    CHECK_EQ(redact_text(d, term, NULL), EXCISE_ERR_INVALID_ARG);
    CHECK_EQ(save_path(d, NULL), EXCISE_ERR_INVALID_ARG);
    CHECK_EQ(save_path(d, "/nonexistent/dir/out.pdf"), EXCISE_ERR_IO);
    CHECK_EQ(save_bytes(d, NULL, &tl), EXCISE_ERR_INVALID_ARG);
    free_buffer(NULL); /* must be a no-op */

    /* handle validity */
    CHECK_EQ(page_count(NULL, &pages), EXCISE_ERR_INVALID_HANDLE);
    CHECK_EQ(page_count((excise_doc*)0xdeadbeef, &pages), EXCISE_ERR_INVALID_HANDLE);
    CHECK_EQ(close_doc(NULL), EXCISE_ERR_INVALID_HANDLE);
    CHECK_EQ(close_doc(d), EXCISE_OK);
    CHECK_EQ(close_doc(d), EXCISE_ERR_INVALID_HANDLE); /* double close */
    CHECK_EQ(page_count(d, &pages), EXCISE_ERR_INVALID_HANDLE); /* use after close */
    CHECK_EQ(save_path(d, out_path), EXCISE_ERR_INVALID_HANDLE);

    /* an in-memory round trip of a file we did not write */
    size_t flen = 0;
    uint8_t* fbytes = read_file(pdf, &flen);
    CHECK(fbytes != NULL);
    excise_doc* m = NULL;
    CHECK_EQ(open_bytes(fbytes, flen, NULL, &m), EXCISE_OK);
    free(fbytes); /* the library copied the input */
    CHECK_EQ(page_count(m, &pages), EXCISE_OK);
    CHECK_EQ(close_doc(m), EXCISE_OK);

    if (g_failed) {
        fprintf(stderr, "FAILED %d of %d checks\n", g_failed, g_checks);
        return 1;
    }
    printf("PASSED %d\n", g_checks);
    return 0;
}
