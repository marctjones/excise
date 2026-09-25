"""ctypes test for the excise C ABI (Excise.Native).

    EXCISE_NATIVE_LIB=dist/native/osx-arm64/libexcise_native.dylib python3 -m unittest -v test_ctypes

The redaction round trip is verified with an INDEPENDENT tool (mutool, else pdftotext):
excise's own extraction is never the oracle for excise's own redaction.
"""
import ctypes
import os
import shutil
import subprocess
import tempfile
import threading
import unittest
from ctypes import POINTER, byref, c_char_p, c_double, c_int32, c_size_t, c_uint8, c_void_p

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
CORPUS = os.environ.get("EXCISE_TEST_PDFS", os.path.join(REPO, "test-pdfs", "smoke"))
SAMPLE = os.path.join(CORPUS, "irs-w9.pdf")
TERM = "Taxpayer"  # on page 1 of irs-w9.pdf

OK, INVALID_ARG, BAD_PDF, PASSWORD, IO, UNSUPPORTED, INVALID_HANDLE, PAGE_RANGE = 0, 1, 2, 3, 4, 5, 6, 7
BUSY, INCOMPLETE = 9, 10

LIB_PATH = os.environ.get("EXCISE_NATIVE_LIB")


def load():
    lib = ctypes.CDLL(LIB_PATH)
    p_doc = POINTER(c_void_p)
    sigs = {
        "excise_abi_version": (ctypes.c_int, []),
        "excise_version": (c_char_p, []),
        "excise_last_error": (c_char_p, []),
        "excise_free_buffer": (None, [c_void_p]),
        "excise_open_bytes": (ctypes.c_int, [c_char_p, c_size_t, c_char_p, p_doc]),
        "excise_open_path": (ctypes.c_int, [c_char_p, c_char_p, p_doc]),
        "excise_close": (ctypes.c_int, [c_void_p]),
        "excise_page_count": (ctypes.c_int, [c_void_p, POINTER(c_int32)]),
        "excise_page_size": (ctypes.c_int, [c_void_p, c_int32, POINTER(c_double), POINTER(c_double)]),
        "excise_extract_text": (ctypes.c_int, [c_void_p, c_int32, POINTER(POINTER(c_uint8)), POINTER(c_size_t)]),
        "excise_redact_text": (ctypes.c_int, [c_void_p, c_char_p, POINTER(c_int32)]),
        "excise_redact_area": (ctypes.c_int, [c_void_p, c_int32, c_double, c_double, c_double, c_double]),
        "excise_save_path": (ctypes.c_int, [c_void_p, c_char_p]),
        "excise_save_bytes": (ctypes.c_int, [c_void_p, POINTER(POINTER(c_uint8)), POINTER(c_size_t)]),
        "excise_encrypt_save_path": (ctypes.c_int, [c_void_p, c_char_p, c_char_p, c_char_p]),
        "excise_decrypt_save_path": (ctypes.c_int, [c_void_p, c_char_p]),
    }
    for name, (res, args) in sigs.items():
        fn = getattr(lib, name)
        fn.restype = res
        fn.argtypes = args
    return lib


def independent_text(path, page=1):
    """Page text from a tool that is not excise. Returns None when no tool is installed."""
    if shutil.which("mutool"):
        r = subprocess.run(["mutool", "draw", "-F", "txt", "-o", "-", path, str(page)],
                           capture_output=True, timeout=120)
        return r.stdout.decode("utf-8", "replace")
    if shutil.which("pdftotext"):
        r = subprocess.run(["pdftotext", "-f", str(page), "-l", str(page), path, "-"],
                           capture_output=True, timeout=120)
        return r.stdout.decode("utf-8", "replace")
    return None


@unittest.skipUnless(LIB_PATH and os.path.exists(LIB_PATH), "EXCISE_NATIVE_LIB not set or missing: build first")
class NativeApiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.lib = load()
        cls.tmp = tempfile.mkdtemp(prefix="excise-native-")

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def open_sample(self, password=None, path=None):
        doc = c_void_p()
        rc = self.lib.excise_open_path((path or SAMPLE).encode(), password, byref(doc))
        self.assertEqual(rc, OK, self.err())
        self.addCleanup(lambda d=doc: self.lib.excise_close(d) if d.value else None)
        return doc

    def err(self):
        return (self.lib.excise_last_error() or b"").decode()

    def text(self, doc, page=1):
        out, n = POINTER(c_uint8)(), c_size_t()
        rc = self.lib.excise_extract_text(doc, page, byref(out), byref(n))
        self.assertEqual(rc, OK, self.err())
        data = ctypes.string_at(out, n.value)
        self.assertEqual(ctypes.string_at(ctypes.addressof(out.contents) + n.value, 1), b"\0")
        self.lib.excise_free_buffer(out)
        return data.decode("utf-8")

    # ---- identity ---------------------------------------------------------------

    def test_version_and_abi(self):
        self.assertEqual(self.lib.excise_abi_version(), 1)
        self.assertIn(b"excise", self.lib.excise_version())

    # ---- queries ----------------------------------------------------------------

    def test_page_count_size_text(self):
        doc = self.open_sample()
        n = c_int32()
        self.assertEqual(self.lib.excise_page_count(doc, byref(n)), OK)
        self.assertGreaterEqual(n.value, 1)
        w, h = c_double(), c_double()
        self.assertEqual(self.lib.excise_page_size(doc, 1, byref(w), byref(h)), OK)
        self.assertAlmostEqual(w.value, 612, delta=2)
        self.assertAlmostEqual(h.value, 792, delta=2)
        self.assertIn(TERM, self.text(doc))

    # ---- full redact round trip, verified by an independent tool ----------------

    @unittest.skipUnless(shutil.which("mutool") or shutil.which("pdftotext"),
                         "no independent text tool (mutool/pdftotext) installed: cannot verify redaction")
    def test_redact_round_trip_verified_independently(self):
        # The oracle must be able to see the term in the original, or "not found" proves nothing.
        self.assertIn(TERM, independent_text(SAMPLE))

        doc = self.open_sample()
        removed = c_int32()
        self.assertEqual(self.lib.excise_redact_text(doc, TERM.encode(), byref(removed)), OK, self.err())
        self.assertGreaterEqual(removed.value, 1)
        out_path = os.path.join(self.tmp, "redacted-ü.pdf")  # non-ASCII path exercises UTF-8
        self.assertEqual(self.lib.excise_save_path(doc, out_path.encode()), OK, self.err())

        self.assertNotIn(TERM, independent_text(out_path))
        # Not a blank page: other text survived.
        self.assertIn("Request for", independent_text(out_path))
        # The whole file, every page, not only page 1.
        if shutil.which("mutool"):
            r = subprocess.run(["mutool", "draw", "-F", "txt", "-o", "-", out_path], capture_output=True, timeout=300)
            self.assertNotIn(TERM, r.stdout.decode("utf-8", "replace"))

    @unittest.skipUnless(shutil.which("mutool") or shutil.which("pdftotext"),
                         "no independent text tool installed")
    def test_save_bytes_matches_and_redacted(self):
        doc = self.open_sample()
        removed = c_int32()
        self.assertEqual(self.lib.excise_redact_text(doc, TERM.encode(), byref(removed)), OK, self.err())
        out, n = POINTER(c_uint8)(), c_size_t()
        self.assertEqual(self.lib.excise_save_bytes(doc, byref(out), byref(n)), OK, self.err())
        data = ctypes.string_at(out, n.value)
        self.lib.excise_free_buffer(out)
        self.assertTrue(data.startswith(b"%PDF-"))
        p = os.path.join(self.tmp, "from-bytes.pdf")
        with open(p, "wb") as f:
            f.write(data)
        self.assertNotIn(TERM, independent_text(p))

    def test_redact_area_removes_text(self):
        doc = self.open_sample()
        w, h = c_double(), c_double()
        self.lib.excise_page_size(doc, 1, byref(w), byref(h))
        self.assertEqual(self.lib.excise_redact_area(doc, 1, 0, 0, w.value, h.value), OK, self.err())
        self.assertNotIn(TERM, self.text(doc))

    # ---- encryption -------------------------------------------------------------

    def test_encrypt_decrypt_and_passwords(self):
        doc = self.open_sample()
        enc = os.path.join(self.tmp, "enc.pdf")
        self.assertEqual(self.lib.excise_encrypt_save_path(doc, enc.encode(), b"s3cret", None), OK, self.err())
        if shutil.which("mutool"):  # independent confirmation: locked without the password, readable with it
            locked = subprocess.run(["mutool", "info", enc], capture_output=True, text=True, timeout=60)
            self.assertIn("authenticate", locked.stdout + locked.stderr)
            opened = subprocess.run(["mutool", "draw", "-p", "s3cret", "-F", "txt", "-o", "-", enc, "1"],
                                    capture_output=True, timeout=120)
            self.assertIn(TERM, opened.stdout.decode("utf-8", "replace"))

        d = c_void_p()
        self.assertEqual(self.lib.excise_open_path(enc.encode(), None, byref(d)), PASSWORD)
        self.assertFalse(d.value)
        self.assertTrue(self.err())
        self.assertEqual(self.lib.excise_open_path(enc.encode(), b"wrong", byref(d)), PASSWORD)
        good = self.open_sample(password=b"s3cret", path=enc)
        self.assertIn(TERM, self.text(good))

        # save of an encrypted source stays encrypted, decrypt_save writes it open
        again = os.path.join(self.tmp, "again.pdf")
        self.assertEqual(self.lib.excise_save_path(good, again.encode()), OK, self.err())
        self.assertEqual(self.lib.excise_open_path(again.encode(), None, byref(d)), PASSWORD)
        dec = os.path.join(self.tmp, "dec.pdf")
        self.assertEqual(self.lib.excise_decrypt_save_path(good, dec.encode()), OK, self.err())
        plain = self.open_sample(path=dec)
        self.assertIn(TERM, self.text(plain))

        self.assertEqual(self.lib.excise_encrypt_save_path(doc, enc.encode(), None, None), INVALID_ARG)

    # ---- error paths ------------------------------------------------------------

    def test_error_paths_and_null_pointers(self):
        d = c_void_p(1)
        self.assertEqual(self.lib.excise_open_bytes(b"not a pdf at all", 16, None, byref(d)), BAD_PDF)
        self.assertFalse(d.value)
        self.assertTrue(self.err())
        self.assertEqual(self.lib.excise_open_bytes(None, 5, None, byref(d)), INVALID_ARG)
        self.assertEqual(self.lib.excise_open_bytes(b"x", 1, None, None), INVALID_ARG)
        self.assertEqual(self.lib.excise_open_path(None, None, byref(d)), INVALID_ARG)
        self.assertEqual(self.lib.excise_open_path(b"/nonexistent/x.pdf", None, byref(d)), IO)

        doc = self.open_sample()
        n = c_int32()
        w, h = c_double(), c_double()
        self.assertEqual(self.lib.excise_page_count(doc, None), INVALID_ARG)
        self.assertEqual(self.lib.excise_page_size(doc, 0, byref(w), byref(h)), PAGE_RANGE)
        self.assertEqual(self.lib.excise_page_size(doc, 10_000, byref(w), byref(h)), PAGE_RANGE)
        out, ln = POINTER(c_uint8)(), c_size_t()
        self.assertEqual(self.lib.excise_extract_text(doc, 10_000, byref(out), byref(ln)), PAGE_RANGE)
        self.assertEqual(self.lib.excise_redact_text(doc, None, byref(n)), INVALID_ARG)
        self.assertEqual(self.lib.excise_redact_text(doc, b"", byref(n)), INVALID_ARG)
        self.assertEqual(self.lib.excise_redact_area(doc, 10_000, 0, 0, 1, 1), PAGE_RANGE)
        self.assertEqual(self.lib.excise_redact_area(doc, 1, float("nan"), 0, 1, 1), INVALID_ARG)
        self.assertEqual(self.lib.excise_save_path(doc, None), INVALID_ARG)
        self.assertEqual(self.lib.excise_save_path(doc, b"/nonexistent/dir/o.pdf"), IO)

    def test_handle_misuse(self):
        n = c_int32()
        self.assertEqual(self.lib.excise_page_count(None, byref(n)), INVALID_HANDLE)
        self.assertEqual(self.lib.excise_page_count(c_void_p(0xDEADBEEF), byref(n)), INVALID_HANDLE)
        self.assertEqual(self.lib.excise_close(None), INVALID_HANDLE)
        doc = c_void_p()
        self.assertEqual(self.lib.excise_open_path(SAMPLE.encode(), None, byref(doc)), OK)
        stale = c_void_p(doc.value)
        self.assertEqual(self.lib.excise_close(doc), OK)
        self.assertEqual(self.lib.excise_close(stale), INVALID_HANDLE)  # double close
        self.assertEqual(self.lib.excise_page_count(stale, byref(n)), INVALID_HANDLE)  # use after close
        self.assertTrue(self.err())

    # ---- threads ----------------------------------------------------------------

    def test_independent_handles_on_threads(self):
        results, errors = [], []

        def work():
            try:
                doc = c_void_p()
                assert self.lib.excise_open_path(SAMPLE.encode(), None, byref(doc)) == OK
                removed = c_int32()
                rc = self.lib.excise_redact_text(doc, TERM.encode(), byref(removed))
                assert rc == OK, self.err()
                results.append(removed.value)
                assert self.lib.excise_close(doc) == OK
                # last_error is per thread: a failure here must not be visible from the main thread
                self.lib.excise_page_count(None, byref(c_int32()))
            except Exception as e:  # noqa: BLE001
                errors.append(repr(e))

        threads = [threading.Thread(target=work) for _ in range(4)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        self.assertEqual(errors, [])
        self.assertEqual(len(results), 4)
        self.assertTrue(all(r >= 1 for r in results))


if __name__ == "__main__":
    unittest.main()
