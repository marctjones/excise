// iText pdfSweep competitor adapter (#1121) — the best-known DEDICATED redactor,
// and a measured width-leaker (PETS 2023), so a calibrated reference point.
// Run as a single-file source launch, never linked:
//
//   java --class-path <tools/vendor/itext/*.jar joined> \
//        scripts/ItextRedactor.java <in.pdf> <out.pdf> <term>
//
// pdfSweep's autoSweep finds the term by regex and CLEANS the region (removes
// content and covers it) — content-level redaction, iText's documented best
// mode for this, not a minimal one. Prints the occurrence count to stdout.
import com.itextpdf.kernel.pdf.PdfDocument;
import com.itextpdf.kernel.pdf.PdfReader;
import com.itextpdf.kernel.pdf.PdfWriter;
import com.itextpdf.kernel.pdf.canvas.parser.PdfTextExtractor;
import com.itextpdf.pdfcleanup.PdfCleaner;
import com.itextpdf.pdfcleanup.autosweep.CompositeCleanupStrategy;
import com.itextpdf.pdfcleanup.autosweep.RegexBasedCleanupStrategy;

import java.util.regex.Matcher;
import java.util.regex.Pattern;

public class ItextRedactor {
    public static void main(String[] args) throws Exception {
        if (args.length < 3) { System.err.println("usage: <in> <out> <term>"); System.exit(2); }
        String in = args[0], out = args[1], term = args[2];

        try (PdfDocument pdf = new PdfDocument(new PdfReader(in), new PdfWriter(out))) {
            Pattern p = Pattern.compile(Pattern.quote(term));

            // Count occurrences BEFORE cleanup, from the same document — a
            // reference that reports 0 for a visible term is a broken run.
            int count = 0;
            for (int i = 1; i <= pdf.getNumberOfPages(); i++) {
                String t = PdfTextExtractor.getTextFromPage(pdf.getPage(i));
                Matcher m = p.matcher(t);
                while (m.find()) count++;
            }

            CompositeCleanupStrategy strategy = new CompositeCleanupStrategy();
            strategy.add(new RegexBasedCleanupStrategy(p));
            PdfCleaner.autoSweepCleanUp(pdf, strategy);

            System.out.println(count);
        }
    }
}
