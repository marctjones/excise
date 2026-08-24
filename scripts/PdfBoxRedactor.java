// PDFBox reference redactor (#1042) — a SECOND, genuinely independent
// implementation of "remove the term's glyphs" for cross-checking excise.
// Different language, different engine from the mutool oracle (which shares
// MuPDF's blind spots). Run as a single-file source launch, never linked:
//
//   java --class-path tools/vendor/pdfbox-app-3.0.3.jar \
//        scripts/PdfBoxRedactor.java <in.pdf> <out.pdf> <term>
//
// It tokenises each page's content stream, tracks the current font, decodes
// every show-text operator (Tj/TJ/'/") through that font, and DROPS the whole
// operator when its decoded text contains the term. Whole-operator removal is
// coarser than excise's glyph-level path, which is fine and deliberate: the
// comparison assertion is one-directional — excise may remove LESS collateral
// than this reference, never more. Prints the hit count to stdout.
import org.apache.pdfbox.Loader;
import org.apache.pdfbox.cos.*;
import org.apache.pdfbox.contentstream.operator.Operator;
import org.apache.pdfbox.pdfparser.PDFStreamParser;
import org.apache.pdfbox.pdfwriter.ContentStreamWriter;
import org.apache.pdfbox.pdmodel.*;
import org.apache.pdfbox.pdmodel.common.PDStream;
import org.apache.pdfbox.pdmodel.font.PDFont;

import java.io.*;
import java.util.*;

public class PdfBoxRedactor {
    public static void main(String[] args) throws Exception {
        if (args.length < 3) { System.err.println("usage: <in> <out> <term>"); System.exit(2); }
        File in = new File(args[0]), out = new File(args[1]);
        String term = args[2];
        int hits = 0;

        try (PDDocument doc = Loader.loadPDF(in)) {
            for (PDPage page : doc.getPages()) {
                PDResources res = page.getResources();
                if (res == null) continue;

                List<Object> tokens = new ArrayList<>();
                PDFStreamParser parser = new PDFStreamParser(page);
                Object t;
                while ((t = parser.parseNextToken()) != null) tokens.add(t);

                List<Object> outTokens = new ArrayList<>();
                List<Object> operands = new ArrayList<>();
                PDFont font = null;
                boolean changed = false;

                for (Object tok : tokens) {
                    if (!(tok instanceof Operator)) { operands.add(tok); continue; }
                    String op = ((Operator) tok).getName();

                    if ("Tf".equals(op) && operands.size() >= 1 && operands.get(0) instanceof COSName) {
                        try { font = res.getFont((COSName) operands.get(0)); } catch (Exception e) { font = null; }
                        outTokens.addAll(operands); outTokens.add(tok); operands.clear(); continue;
                    }

                    boolean isShow = "Tj".equals(op) || "TJ".equals(op) || "'".equals(op) || "\"".equals(op);
                    if (isShow && font != null && decodedContains(op, operands, font, term)) {
                        hits++; changed = true; operands.clear();   // drop the operator + its operands
                        continue;
                    }

                    outTokens.addAll(operands); outTokens.add(tok); operands.clear();
                }

                if (changed) {
                    PDStream repl = new PDStream(doc);
                    try (OutputStream os = repl.createOutputStream(COSName.FLATE_DECODE)) {
                        new ContentStreamWriter(os).writeTokens(outTokens);
                    }
                    page.setContents(repl);
                }
            }
            doc.save(out);
        }
        System.out.println(hits);
    }

    // Decode a show-text operator's string operand(s) through the current font
    // and test for the term. TJ carries a COSArray of strings and kerning
    // numbers; " carries [aw ac string]; ' and Tj carry a single string.
    private static boolean decodedContains(String op, List<Object> operands, PDFont font, String term) {
        StringBuilder sb = new StringBuilder();
        try {
            if ("TJ".equals(op) && !operands.isEmpty() && operands.get(operands.size() - 1) instanceof COSArray) {
                for (COSBase e : (COSArray) operands.get(operands.size() - 1))
                    if (e instanceof COSString) decodeString((COSString) e, font, sb);
            } else {
                for (Object o : operands)
                    if (o instanceof COSString) decodeString((COSString) o, font, sb);
            }
        } catch (Exception e) { return false; }
        return sb.toString().contains(term);
    }

    private static void decodeString(COSString s, PDFont font, StringBuilder sb) throws IOException {
        byte[] bytes = s.getBytes();
        try (InputStream in = new ByteArrayInputStream(bytes)) {
            while (in.available() > 0) {
                int code = font.readCode(in);
                String u = font.toUnicode(code);
                if (u != null) sb.append(u);
            }
        }
    }
}
